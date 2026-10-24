using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Persistence;
using System.Globalization;

namespace QuestionAppBuilder.Infrastructure.Responses;

public sealed class SubmissionCoordinator : ISubmissionCoordinator
{
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;
    private readonly IRespondentContext _identity;
    private readonly IDefinitionEvaluator _evaluator;

    public SubmissionCoordinator(IDbContextFactory<QuestionAppBuilderDbContext> factory, IRespondentContext identity, IDefinitionEvaluator evaluator)
    { _factory = factory; _identity = identity; _evaluator = evaluator; }

    public async Task<SubmissionReceipt> SubmitAsync(SubmitResponseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.ResponseId == Guid.Empty || request.ExpectedRevision is null)
            throw new ArgumentException("A response, revision, and non-empty request id are required.", nameof(request));
        var identity = _identity.GetRequiredIdentity();
        if (string.IsNullOrWhiteSpace(identity.OwnerKey)) throw new InvalidOperationException("A respondent identity is required.");
        await using var strategyContext = await _factory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var responseHead = await db.ResponseDrafts.AsNoTracking().Where(r => r.Id == request.ResponseId)
                .Select(r => new { r.Id, r.ApplicationId, r.OwnerKey }).SingleOrDefaultAsync(cancellationToken);
            if (responseHead is null || !await CanAccessAsync(db, identity, responseHead.Id, responseHead.OwnerKey, cancellationToken))
                throw new NotFoundException("The response was not found.");
            await SqlApplicationLock.AcquireAsync(db, responseHead.ApplicationId, cancellationToken);

            var prior = await db.Submissions.AsNoTracking().Include(s => s.CategoryScores)
                .SingleOrDefaultAsync(s => s.ResponseId == request.ResponseId, cancellationToken);
            if (prior is not null)
            {
                if (prior.RequestId != request.RequestId) throw new ResponseConflictException();
                var priorVersion = await LoadReceiptVersionAsync(db, prior.ApplicationVersionId, cancellationToken);
                return await CreateReceiptAsync(db, prior, priorVersion, cancellationToken);
            }

            var response = await db.ResponseDrafts.Include(r => r.Answers).ThenInclude(a => a.SelectedOptions)
                .SingleAsync(r => r.Id == request.ResponseId, cancellationToken);
            if (response.Status != ResponseStatus.InProgress || !response.RevisionNonce.ToByteArray().AsSpan().SequenceEqual(request.ExpectedRevision))
                throw new ResponseConflictException();
            var app = await db.Applications.SingleAsync(a => a.Id == response.ApplicationId, cancellationToken);
            if (!app.AcceptSubmissions) throw new InvalidOperationException("This questionnaire is not accepting submissions.");
            var version = await db.ApplicationVersions.AsNoTracking().Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Options)
                .Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Rules).ThenInclude(rule => rule.Options)
                .Include(v => v.VisibilityConditions).ThenInclude(c => c.Predicates).ThenInclude(p => p.Options)
                .SingleAsync(v => v.Id == response.ApplicationVersionId && v.Status == VersionStatus.Published, cancellationToken);
            var graph = new QuestionAppBuilder.Domain.Graphs.ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions };
            var values = response.Answers.ToDictionary(a => a.QuestionId, a =>
            {
                var selected = a.SelectedOptions.Select(o => o.QuestionOptionId).ToArray();
                return new AnswerValue(a.TextValue, a.NumberValue, selected.Length == 0 ? null : selected);
            });
            var evaluated = _evaluator.Evaluate(graph, values, requireComplete: true);
            if (evaluated.DefinitionIssues.Count != 0) throw new DefinitionValidationException(string.Join(" ", evaluated.DefinitionIssues.Select(i => i.Message)));
            if (evaluated.AnswerIssues.Count != 0) throw new AnswerValidationException(string.Join(" ", evaluated.AnswerIssues.Select(i => i.Message)), evaluated.AnswerIssues.Where(i => i.QuestionId.HasValue).Select(i => i.QuestionId!.Value).Distinct().ToArray());

            var compositionSnapshot = evaluated.Composition is null ? null : evaluated.Composition with
            { VisibleCategoryIds = evaluated.VisibleCategoryIds.OrderBy(id => id).ToArray() };
            var compositionSnapshotJson = System.Text.Json.JsonSerializer.Serialize(compositionSnapshot);
            var now = evaluated.Composition?.DynamicText?.EvaluatedAtUtc.UtcDateTime ?? DateTime.UtcNow;
            var submission = new Submission { ApplicationId = response.ApplicationId, ApplicationVersionId = version.Id, UserId = identity.OwnerKey,
                ResponseId = response.Id, RequestId = request.RequestId, SubmittedUtc = now, TotalScore = evaluated.Score.Total, EngineVersion = evaluated.Score.EngineVersion };
            var allQuestions = version.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!).ToArray();
            var scores = evaluated.Score.Questions.ToDictionary(q => q.QuestionId);
            foreach (var question in allQuestions)
            {
                var applicable = evaluated.VisibleQuestionIds.Contains(question.Id);
                var score = scores.GetValueOrDefault(question.Id);
                submission.QuestionStates.Add(new SubmissionQuestionState { QuestionId = question.Id, QuestionLogicalKey = version.Categories.SelectMany(c => c.Elements).Single(e => e.Question?.Id == question.Id).LogicalKey,
                    IsApplicable = applicable, PointsEarned = applicable ? score?.Points ?? 0 : 0 });
                if (applicable && values.TryGetValue(question.Id, out var answer))
                {
                    var stored = new Answer { QuestionId = question.Id, TextValue = answer.Text, NumberValue = answer.Number, PointsEarned = score?.Points ?? 0 };
                    foreach (var optionId in answer.SelectedOptionIds ?? Array.Empty<int>()) stored.SelectedOptions.Add(new AnswerSelectedOption { QuestionOptionId = optionId });
                    submission.Answers.Add(stored);
                }
                if (applicable && score is not null)
                    foreach (var rule in score.Rules) submission.RuleTraces.Add(new SubmissionRuleTrace { QuestionId = question.Id, RuleId = rule.RuleId, Matches = rule.Matches, Contributes = rule.Contributes, Points = rule.Points });
            }
            foreach (var category in evaluated.Score.Categories)
                submission.CategoryScores.Add(new SubmissionCategoryScore { CategoryId = category.CategoryId, Score = category.Score });
            submission.CompositionSnapshotJson = compositionSnapshotJson;
            db.Submissions.Add(submission);
            // Retain the submitted attachments, and discard files from branches that no longer apply.
            var hiddenDocumentIds = allQuestions.Where(q => q.AnswerType == AnswerType.UploadDocument
                && !evaluated.VisibleQuestionIds.Contains(q.Id)).Select(q => q.Id).ToArray();
            if (hiddenDocumentIds.Length != 0)
                await db.ResponseDocuments.Where(d => d.ResponseDraftId == response.Id && hiddenDocumentIds.Contains(d.QuestionId))
                    .ExecuteDeleteAsync(cancellationToken);
            response.Answers.Clear();
            response.Status = ResponseStatus.Submitted;
            response.RevisionNonce = Guid.NewGuid();
            response.UpdatedUtc = now;
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException ex) { throw new ResponseConflictException(ex); }
            await transaction.CommitAsync(cancellationToken);
            return await CreateReceiptAsync(db, submission, version, cancellationToken);
        });
    }

    public async Task<SubmissionReceipt> GetReceiptAsync(Guid responseId, CancellationToken cancellationToken = default)
    {
        var identity = _identity.GetRequiredIdentity();
        if (string.IsNullOrWhiteSpace(identity.OwnerKey)) throw new InvalidOperationException("A respondent identity is required.");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var response = await db.ResponseDrafts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == responseId, cancellationToken);
        if (response is null || !await CanAccessAsync(db, identity, response.Id, response.OwnerKey, cancellationToken))
            throw new NotFoundException("The response was not found.");
        var submission = await db.Submissions.AsNoTracking().Include(s => s.CategoryScores)
            .SingleOrDefaultAsync(s => s.ResponseId == responseId, cancellationToken)
            ?? throw new NotFoundException("The receipt was not found.");
        var version = await LoadReceiptVersionAsync(db, submission.ApplicationVersionId, cancellationToken);
        return await CreateReceiptAsync(db, submission, version, cancellationToken);
    }

    public async Task<SubmissionReceipt> GetSubmittedApplicationAsync(Guid responseId, CancellationToken cancellationToken = default)
    {
        var identity = _identity.GetRequiredIdentity();
        if (string.IsNullOrWhiteSpace(identity.OwnerKey)) throw new InvalidOperationException("A respondent identity is required.");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var response = await db.ResponseDrafts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == responseId, cancellationToken);
        if (response is null || !await CanAccessAsync(db, identity, response.Id, response.OwnerKey, cancellationToken))
            throw new NotFoundException("The response was not found.");
        var submission = await db.Submissions.AsNoTracking().Include(item => item.CategoryScores)
            .SingleOrDefaultAsync(item => item.ResponseId == responseId, cancellationToken)
            ?? throw new NotFoundException("The submitted application was not found.");
        var version = await LoadFullSubmittedVersionAsync(db, submission.ApplicationVersionId, cancellationToken);
        var receipt = await CreateReceiptAsync(db, submission, version, cancellationToken, includeAnswerReview: false);
        var fullSubmission = await LoadSubmittedAnswersAsync(db, submission.Id, cancellationToken);
        var showScores = version.SubmittedApplicationVisibility == SubmittedApplicationVisibility.WithScores;
        var application = await BuildSubmittedApplicationAsync(db, fullSubmission, version,
            ReadCompositionSnapshot(submission.CompositionSnapshotJson), showScores, cancellationToken);
        return receipt with { Total = showScores ? submission.TotalScore : null, Categories = null, SubmittedApplication = application };
    }

    private static Task<Submission> LoadSubmittedAnswersAsync(QuestionAppBuilderDbContext db, int submissionId,
        CancellationToken cancellationToken)
        => db.Submissions.AsNoTracking().AsSplitQuery().Include(item => item.Answers).ThenInclude(answer => answer.SelectedOptions)
            .Include(item => item.QuestionStates).Include(item => item.CategoryScores)
            .SingleAsync(item => item.Id == submissionId, cancellationToken);

    private static CompositionResult? ReadCompositionSnapshot(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<CompositionResult>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static async Task<ApplicationVersion> LoadReceiptVersionAsync(QuestionAppBuilderDbContext db, int versionId,
        CancellationToken cancellationToken)
    {
        var version = await db.ApplicationVersions.AsNoTracking().SingleAsync(item => item.Id == versionId, cancellationToken);
        if (version.ResultVisibility != ResultVisibility.ConfirmationAndAnswerReview) return version;
        return await LoadFullSubmittedVersionAsync(db, versionId, cancellationToken);
    }

    private static Task<ApplicationVersion> LoadFullSubmittedVersionAsync(QuestionAppBuilderDbContext db, int versionId,
        CancellationToken cancellationToken)
        => db.ApplicationVersions.AsNoTracking().AsSplitQuery().Include(item => item.Categories).ThenInclude(category => category.Elements)
            .ThenInclude(element => element.Question).ThenInclude(question => question!.Options)
            .SingleAsync(item => item.Id == versionId, cancellationToken);

    private static async Task<SubmissionReceipt> CreateReceiptAsync(QuestionAppBuilderDbContext db, Submission s,
        ApplicationVersion v, CancellationToken cancellationToken, bool includeAnswerReview = true)
    {
        var snapshot = ReadCompositionSnapshot(s.CompositionSnapshotJson);
        var confirmationText = snapshot?.DynamicText?.Texts
            .LastOrDefault(text => text.Slot == DynamicTextSlot.ConfirmationText && text.OwnerKey == Guid.Empty);
        var confirmation = confirmationText is null ? v.ConfirmationText : confirmationText.IsHidden ? string.Empty : confirmationText.Text;
        var summaryText = snapshot?.DynamicText?.Texts
            .LastOrDefault(text => text.Slot == DynamicTextSlot.ReviewSummary && text.OwnerKey == Guid.Empty);
        var receipt = v.ResultVisibility switch
        {
            ResultVisibility.ConfirmationOnly => new SubmissionReceipt(s.Id, s.ResponseId!.Value, s.SubmittedUtc, confirmation, null, null),
            ResultVisibility.CategoryBreakdown => new SubmissionReceipt(s.Id, s.ResponseId!.Value, s.SubmittedUtc, confirmation, s.TotalScore,
                s.CategoryScores.Select(c => new CategoryScore(c.CategoryId, c.Score)).ToArray()),
            ResultVisibility.ConfirmationAndAnswerReview => new SubmissionReceipt(s.Id, s.ResponseId!.Value, s.SubmittedUtc, confirmation, s.TotalScore, null),
            _ => new SubmissionReceipt(s.Id, s.ResponseId!.Value, s.SubmittedUtc, confirmation, s.TotalScore, null)
        };
        SubmittedApplication? submittedApplication = null;
        if (includeAnswerReview && v.ResultVisibility == ResultVisibility.ConfirmationAndAnswerReview)
        {
            var submitted = await LoadSubmittedAnswersAsync(db, s.Id, cancellationToken);
            submittedApplication = await BuildSubmittedApplicationAsync(db, submitted, v, snapshot, true, cancellationToken);
        }
        return receipt with { ReviewSummary = summaryText is { IsHidden: false } ? summaryText.Text : null,
            SubmittedApplication = submittedApplication };
    }

    private static async Task<SubmittedApplication> BuildSubmittedApplicationAsync(QuestionAppBuilderDbContext db,
        Submission submission, ApplicationVersion version, CompositionResult? snapshot, bool showScores, CancellationToken cancellationToken)
    {
        var categories = version.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToArray();
        var states = submission.QuestionStates.ToDictionary(state => state.QuestionId);
        var savedAnswers = submission.Answers.ToDictionary(answer => answer.QuestionId);
        var visibleCategoryIds = snapshot?.VisibleCategoryIds is { } frozenCategories
            ? frozenCategories.ToHashSet()
            : categories.Where(category => category.VisibilityConditionId is null
                || category.Elements.Any(element => element.Question is { } question
                    && states.TryGetValue(question.Id, out var state) && state.IsApplicable))
                .Select(category => category.Id).ToHashSet();
        var dynamicTexts = snapshot?.DynamicText?.Texts ?? [];
        var resolvedTexts = dynamicTexts.GroupBy(text => (text.Slot, text.OwnerKey))
            .ToDictionary(group => group.Key, group => group.Last());
        string Resolve(DynamicTextSlot slot, Guid key, string? fallback)
            => resolvedTexts.TryGetValue((slot, key), out var text) ? text.Text : fallback ?? string.Empty;
        string? ResolveHelp(Element element)
            => resolvedTexts.TryGetValue((DynamicTextSlot.ElementHelpText, element.LogicalKey), out var text)
                ? text.IsHidden ? null : string.IsNullOrEmpty(text.Text) ? null : text.Text
                : element.HelpText;
        bool HiddenLabel(Element element)
            => resolvedTexts.TryGetValue((DynamicTextSlot.ElementLabel, element.LogicalKey), out var text) && text.IsHidden;

        var uploadQuestionIds = categories.SelectMany(category => category.Elements)
            .Where(element => element.Question?.AnswerType == AnswerType.UploadDocument && states.TryGetValue(element.Question.Id, out var state) && state.IsApplicable)
            .Select(element => element.Question!.Id).ToArray();
        var filenames = uploadQuestionIds.Length == 0 ? new Dictionary<int, string>() : await db.ResponseDocuments.AsNoTracking()
            .Where(document => document.ResponseDraftId == submission.ResponseId && uploadQuestionIds.Contains(document.QuestionId))
            .Select(document => new { document.QuestionId, document.FileName })
            .ToDictionaryAsync(document => document.QuestionId, document => document.FileName, cancellationToken);
        var optionLabels = categories.SelectMany(category => category.Elements).Where(element => element.Question is not null)
            .SelectMany(element => element.Question!.Options).ToDictionary(option => option.Id, option => option.Label);
        var answerTexts = new Dictionary<int, string?>();
        foreach (var element in categories.SelectMany(category => category.Elements))
        {
            if (element.Question is not { } question || !states.TryGetValue(question.Id, out var state) || !state.IsApplicable
                || !savedAnswers.TryGetValue(question.Id, out var answer)) continue;
            answerTexts[question.Id] = FormatAnswer(question, answer, optionLabels, filenames);
        }

        var composition = new CompositionDefinition();
        try { composition = CompositionDefinition.Read(version.CompositionJson); }
        catch (System.Text.Json.JsonException) { }
        var categoryScores = submission.CategoryScores.ToDictionary(score => score.CategoryId, score => score.Score);
        var categoryDtos = new List<SubmittedCategory>();
        foreach (var category in categories.Where(category => visibleCategoryIds.Contains(category.Id)))
        {
            var orderedElements = FollowUpStructure.Ordered(category.Elements);
            var retainedElements = orderedElements.Where(element => !HiddenLabel(element)
                && (element.Question is null || states.TryGetValue(element.Question.Id, out var state) && state.IsApplicable)).ToArray();
            var elementDtos = retainedElements.Select(element =>
            {
                SubmittedQuestion? questionDto = null;
                if (element.Question is { } question)
                {
                    states.TryGetValue(question.Id, out var state);
                    questionDto = new SubmittedQuestion(question.Id, question.AnswerType, answerTexts.GetValueOrDefault(question.Id),
                        showScores && state?.PointsEarned > 0 ? state.PointsEarned : null);
                }
                var parent = FollowUpStructure.Parent(element, category.Elements);
                var context = parent is null || HiddenLabel(parent) ? null
                    : $"{Resolve(DynamicTextSlot.ElementLabel, parent.LogicalKey, parent.Label)} / {element.ParentOption?.Label ?? optionLabels.GetValueOrDefault(element.ParentOptionId ?? 0)}";
                return new SubmittedElement(element.Id, element.LogicalKey, element.Kind,
                    Resolve(DynamicTextSlot.ElementLabel, element.LogicalKey, element.Label),
                    ResolveHelp(element),
                    FollowUpStructure.Depth(element, category.Elements), context, questionDto);
            }).ToArray();
            var retainedKeys = retainedElements.Select(element => element.LogicalKey).ToHashSet();
            var groups = (composition.Groups ?? []).Where(group => group is not null && group.CategoryKey == category.LogicalKey
                && group.Rows.Any(row => retainedKeys.Contains(row.QuestionKey)))
                .Select(group => new SubmittedGroup(group.Name, group.TableLayout, group.Columns.ToArray(), group.Rows
                    .Where(row => retainedKeys.Contains(row.QuestionKey))
                    .Select(row =>
                    {
                        var element = retainedElements.FirstOrDefault(item => item.LogicalKey == row.QuestionKey);
                        return new SubmittedGroupRow(element is null ? throw new InvalidOperationException("A submitted group row has no visible question.")
                            : elementDtos.Single(dto => dto.LogicalKey == element.LogicalKey), row.Cells.ToArray());
                    }).ToArray())).ToArray();
            SubmittedAward[] positiveAwards = showScores ? (snapshot?.Awards ?? []).Where(award => award.CategoryKey == category.LogicalKey && award.Points > 0)
                .Select(award => new SubmittedAward(award.Name, award.Points)).ToArray() : Array.Empty<SubmittedAward>();
            SubmittedCalculatedValue[] calculatedValues = showScores ? (snapshot?.Values ?? []).Where(value => value.CategoryKey == category.LogicalKey)
                .Select(value => new SubmittedCalculatedValue(value.Name, value.Value, value.Percentage)).ToArray() : Array.Empty<SubmittedCalculatedValue>();
            categoryDtos.Add(new SubmittedCategory(category.Id, category.Name, elementDtos, groups,
                calculatedValues, showScores && categoryScores.TryGetValue(category.Id, out var points) && points > 0 ? points : null,
                positiveAwards));
        }
        return new SubmittedApplication(version.DisplayName, categoryDtos);
    }

    private static string? FormatAnswer(Question question, Answer answer, IReadOnlyDictionary<int, string> optionLabels,
        IReadOnlyDictionary<int, string> filenames)
    {
        if (question.AnswerType == AnswerType.UploadDocument) return filenames.GetValueOrDefault(question.Id);
        if (question.AnswerType == AnswerType.Number)
            return answer.NumberValue?.ToString("0.############################", CultureInfo.InvariantCulture);
        if (question.AnswerType is AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes)
        {
            var selected = answer.SelectedOptions.Select(option => option.QuestionOptionId).ToHashSet();
            return string.Join(", ", question.Options.OrderBy(option => option.SortOrder).ThenBy(option => option.Id)
                .Where(option => selected.Contains(option.Id)).Select(option => option.Label));
        }
        if (question.AnswerType == AnswerType.Date && DateOnly.TryParseExact(answer.TextValue, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)) return date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        return answer.TextValue;
    }

    private static async Task<bool> CanAccessAsync(QuestionAppBuilderDbContext db, RespondentIdentity identity,
        Guid responseId, string ownerKey, CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(ownerKey, identity.OwnerKey)) return false;
        if (identity.AccessTokenId is not Guid accessTokenId) return true;
        return await db.ResponseAccessTokens.AsNoTracking().AnyAsync(token => token.Id == accessTokenId
            && token.OwnerKey == identity.OwnerKey && token.ResponseId == responseId && token.ConsumedUtc != null, cancellationToken);
    }
}
