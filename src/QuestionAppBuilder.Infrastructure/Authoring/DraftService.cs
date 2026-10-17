using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Authoring;

public sealed class DraftService : IDraftService
{
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;
    private readonly DefinitionValidator _validator;
    private readonly bool _hasEmailSender;

    public DraftService(IDbContextFactory<QuestionAppBuilderDbContext> factory, IDefinitionEvaluator evaluator, bool emailSenderConfigured = false)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _validator = new DefinitionValidator(evaluator);
        _hasEmailSender = emailSenderConfigured;
    }

    public async Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var graph = await LoadDraft(db, applicationId, tracking: false, ct)
            ?? throw new NotFoundException($"No draft version found for application {applicationId}.");
        return Snapshot(graph);
    }

    public async Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedRevision);
        ArgumentNullException.ThrowIfNull(request.Definition);
        return await ExecuteAsync(async db =>
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            var graph = await LoadDraft(db, request.ApplicationId, tracking: true, ct)
                ?? throw new NotFoundException($"No draft version found for application {request.ApplicationId}.");
            EnsureRevision(graph, request.ExpectedRevision);
            if (request.Definition.Version.Id != graph.Version.Id || request.Definition.Version.ApplicationId != request.ApplicationId)
                throw new DefinitionValidationException("The submitted graph does not belong to this application draft.");
            ApplyEditableFields(graph, request.Definition);
            graph.Version.RevisionNonce = Guid.NewGuid();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new DraftConflictException(); }
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Snapshot(graph);
        }, ct);
    }

    public async Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default)
    {
        var snapshot = await GetAsync(applicationId, ct);
        return _validator.Review(snapshot.Definition, snapshot.Revision);
    }

    public async Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteAsync(async db =>
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == request.ApplicationId, ct)
                ?? throw new NotFoundException($"Application {request.ApplicationId} was not found.");
            var graph = await LoadDraft(db, request.ApplicationId, tracking: true, ct)
                ?? throw new DefinitionValidationException("No draft to publish.");
            EnsureRevision(graph, request.ExpectedRevision);
            if (graph.Version.RespondentAccessMode == RespondentAccessMode.EmailLink && !_hasEmailSender)
                throw new DefinitionValidationException("Email-link access requires a configured response email sender.");
            var review = _validator.Review(graph, request.ExpectedRevision);
            if (review.Errors.Count != 0)
                throw new DefinitionValidationException(string.Join(" ", review.Errors.Select(e => e.Message)));
            var requiredWarnings = review.Warnings.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
            if (!requiredWarnings.SetEquals(request.AcknowledgedWarningCodes))
                throw new DefinitionValidationException("Warning acknowledgment does not match the current draft review.");
            graph.Version.Status = VersionStatus.Published;
            graph.Version.PublishedUtc = DateTime.UtcNow;
            graph.Version.RevisionNonce = Guid.NewGuid();
            var firstPublish = application.CurrentVersionId is null;
            application.CurrentVersionId = graph.Version.Id;
            application.UpdatedUtc = DateTime.UtcNow;
            if (firstPublish)
            {
                application.AcceptNewResponses = true;
                application.AcceptSubmissions = true;
            }
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new DraftConflictException(); }
            if (transaction is not null) await transaction.CommitAsync(ct);
            return graph.Version.Id;
        }, ct);
    }

    private async Task<T> ExecuteAsync<T>(Func<QuestionAppBuilderDbContext, Task<T>> operation, CancellationToken ct)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var fresh = await _factory.CreateDbContextAsync(ct);
                return await operation(fresh);
            });
        }
        catch (DbUpdateConcurrencyException) { throw new DraftConflictException(); }
    }

    private static async Task<ApplicationVersionGraph?> LoadDraft(QuestionAppBuilderDbContext db, int applicationId, bool tracking, CancellationToken ct)
    {
        IQueryable<ApplicationVersion> query = db.ApplicationVersions
            .Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Options)
            .Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Rules).ThenInclude(r => r.Options)
            .Include(v => v.VisibilityConditions).ThenInclude(c => c.Predicates).ThenInclude(p => p.Options)
            .Where(v => v.ApplicationId == applicationId && v.Status == VersionStatus.Draft);
        if (!tracking) query = query.AsNoTracking();
        var version = await query.OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct);
        return version is null ? null : new ApplicationVersionGraph
        {
            Version = version,
            Categories = version.Categories,
            VisibilityConditions = version.VisibilityConditions
        };
    }

    private static DraftSnapshot Snapshot(ApplicationVersionGraph graph) => new(graph, graph.Version.RevisionNonce.ToByteArray());

    private static void EnsureRevision(ApplicationVersionGraph graph, byte[] expected)
    {
        if (!graph.Version.RevisionNonce.ToByteArray().AsSpan().SequenceEqual(expected)) throw new DraftConflictException();
    }

    private static void ApplyEditableFields(ApplicationVersionGraph target, ApplicationVersionGraph posted)
    {
        var sourceVersion = posted.Version;
        target.Version.NavigationMode = sourceVersion.NavigationMode;
        target.Version.RespondentAccessMode = sourceVersion.RespondentAccessMode;
        target.Version.ResultVisibility = sourceVersion.ResultVisibility;
        target.Version.SubmitLabel = sourceVersion.SubmitLabel;
        target.Version.ConfirmationText = sourceVersion.ConfirmationText;
        target.Version.DisplayName = sourceVersion.DisplayName;
        ValidateKnownIds(target, posted);
        var categories = target.Categories.ToDictionary(x => x.Id);
        var elements = target.Categories.SelectMany(x => x.Elements).ToDictionary(x => x.Id);
        var questions = target.Categories.SelectMany(x => x.Elements).Where(x => x.Question is not null).Select(x => x.Question!).ToDictionary(x => x.Id);
        var options = questions.Values.SelectMany(x => x.Options).ToDictionary(x => x.Id);
        var rules = questions.Values.SelectMany(x => x.Rules).ToDictionary(x => x.Id);
        var conditions = target.VisibilityConditions.ToDictionary(x => x.Id);
        foreach (var submittedCategory in posted.Categories)
        {
            var category = categories[submittedCategory.Id];
            category.Name = submittedCategory.Name; category.SortOrder = submittedCategory.SortOrder;
            category.VisibilityCondition = submittedCategory.VisibilityConditionId is int cc ? conditions[cc] : null;
            foreach (var submittedElement in submittedCategory.Elements)
            {
                var element = elements[submittedElement.Id];
                element.Label = submittedElement.Label; element.HelpText = submittedElement.HelpText; element.SortOrder = submittedElement.SortOrder;
                element.VisibilityCondition = submittedElement.VisibilityConditionId is int ec ? conditions[ec] : null;
                if (submittedElement.Question is not { } postedQuestion) continue;
                var question = questions[postedQuestion.Id];
                question.AnswerType = postedQuestion.AnswerType; question.IsRequired = postedQuestion.IsRequired; question.ScoringMode = postedQuestion.ScoringMode;
                question.MaxLength = postedQuestion.MaxLength; question.MinValue = postedQuestion.MinValue; question.MaxValue = postedQuestion.MaxValue;
                question.MinSelections = postedQuestion.MinSelections; question.MaxSelections = postedQuestion.MaxSelections;
                foreach (var submittedOption in postedQuestion.Options)
                {
                    var option = options[submittedOption.Id]; option.Label = submittedOption.Label; option.Value = submittedOption.Value; option.SortOrder = submittedOption.SortOrder;
                }
                foreach (var submittedRule in postedQuestion.Rules)
                {
                    var rule = rules[submittedRule.Id]; rule.Family = submittedRule.Family; rule.Points = submittedRule.Points;
                    rule.SortOrder = submittedRule.SortOrder; rule.MinInclusive = submittedRule.MinInclusive; rule.MaxInclusive = submittedRule.MaxInclusive;
                    rule.MembershipOperator = submittedRule.MembershipOperator; rule.MinValue = submittedRule.MinValue; rule.MaxValue = submittedRule.MaxValue;
                    if (submittedRule.Options.Any(o => o.QuestionOptionId != 0 && !options.ContainsKey(o.QuestionOptionId)))
                        throw new DefinitionValidationException("A scoring rule references an option outside this draft.");
                    rule.Options = submittedRule.Options.Select(o => new ScoringRuleOption { QuestionOptionId = o.QuestionOptionId, QuestionOption = options[o.QuestionOptionId] }).ToList();
                }
            }
        }
        foreach (var submittedCondition in posted.VisibilityConditions)
        {
            var condition = conditions[submittedCondition.Id]; condition.Junction = submittedCondition.Junction;
            foreach (var submittedPredicate in submittedCondition.Predicates)
            {
                var predicate = condition.Predicates.Single(p => p.Id == submittedPredicate.Id);
                if (!questions.ContainsKey(submittedPredicate.SourceQuestionId)) throw new DefinitionValidationException("A visibility condition references a question outside this draft.");
                predicate.SourceQuestionId = submittedPredicate.SourceQuestionId; predicate.Operator = submittedPredicate.Operator; predicate.NumberValue = submittedPredicate.NumberValue;
                if (submittedPredicate.Options.Any(o => !options.ContainsKey(o.QuestionOptionId))) throw new DefinitionValidationException("A visibility condition references an option outside this draft.");
                predicate.Options = submittedPredicate.Options.Select(o => new VisibilityPredicateOption { VisibilityPredicateId = predicate.Id, QuestionOptionId = o.QuestionOptionId }).ToList();
            }
        }
    }

    private static void ValidateKnownIds(ApplicationVersionGraph target, ApplicationVersionGraph posted)
    {
        var categoryIds = target.Categories.Select(x => x.Id).ToHashSet();
        var elementIds = target.Categories.SelectMany(x => x.Elements).Select(x => x.Id).ToHashSet();
        var questions = target.Categories.SelectMany(x => x.Elements).Where(x => x.Question is not null).Select(x => x.Question!).ToArray();
        var questionIds = questions.Select(x => x.Id).ToHashSet();
        var optionIds = questions.SelectMany(x => x.Options).Select(x => x.Id).ToHashSet();
        var ruleIds = questions.SelectMany(x => x.Rules).Select(x => x.Id).ToHashSet();
        var conditionIds = target.VisibilityConditions.Select(x => x.Id).ToHashSet();
        if (posted.Categories.Any(x => !categoryIds.Contains(x.Id))
            || posted.Categories.SelectMany(x => x.Elements).Any(x => !elementIds.Contains(x.Id))
            || posted.Categories.SelectMany(x => x.Elements).Where(x => x.Question is not null).Any(x => !questionIds.Contains(x.Question!.Id))
            || posted.Categories.SelectMany(x => x.Elements).Where(x => x.Question is not null).SelectMany(x => x.Question!.Options).Any(x => !optionIds.Contains(x.Id))
            || posted.Categories.SelectMany(x => x.Elements).Where(x => x.Question is not null).SelectMany(x => x.Question!.Rules).Any(x => !ruleIds.Contains(x.Id))
            || posted.VisibilityConditions.Any(x => !conditionIds.Contains(x.Id))
            || posted.Categories.Any(x => x.VisibilityConditionId is int c && !conditionIds.Contains(c))
            || posted.Categories.SelectMany(x => x.Elements).Any(x => x.VisibilityConditionId is int c && !conditionIds.Contains(c)))
            throw new DefinitionValidationException("The submitted graph contains an object outside this application draft.");
    }
}
