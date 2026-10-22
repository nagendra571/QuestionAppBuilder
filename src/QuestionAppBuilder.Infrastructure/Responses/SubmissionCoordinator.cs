using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;

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
                var priorVersion = await db.ApplicationVersions.AsNoTracking().SingleAsync(v => v.Id == prior.ApplicationVersionId, cancellationToken);
                return Receipt(prior, priorVersion);
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

            var compositionSnapshotJson = System.Text.Json.JsonSerializer.Serialize(evaluated.Composition);
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
            return Receipt(submission, version);
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
        var version = await db.ApplicationVersions.AsNoTracking().SingleAsync(v => v.Id == submission.ApplicationVersionId, cancellationToken);
        return Receipt(submission, version);
    }

    private static SubmissionReceipt Receipt(Submission s, ApplicationVersion v)
    {
        CompositionResult? snapshot = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(s.CompositionSnapshotJson))
                snapshot = System.Text.Json.JsonSerializer.Deserialize<CompositionResult>(s.CompositionSnapshotJson);
        }
        catch (System.Text.Json.JsonException) { }
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
            _ => new SubmissionReceipt(s.Id, s.ResponseId!.Value, s.SubmittedUtc, confirmation, s.TotalScore, null)
        };
        return receipt with { ReviewSummary = summaryText is { IsHidden: false } ? summaryText.Text : null };
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
