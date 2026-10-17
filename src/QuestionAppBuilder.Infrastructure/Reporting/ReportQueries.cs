using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Infrastructure.Persistence;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;
using CategoryScoreDto = QuestionAppBuilder.Application.Reporting.SubmissionCategoryScore;

namespace QuestionAppBuilder.Infrastructure.Reporting;

public sealed class ReportQueries : IReportQueries
{
    private const int MaximumSearchLength = 128;
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;

    public ReportQueries(IDbContextFactory<QuestionAppBuilderDbContext> factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<ReportOverview> GetOverviewAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        ValidateFilter(filter);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var submitted = ApplySubmissionFilter(db.Submissions.AsNoTracking(), filter);
        var submittedCount = await submitted.CountAsync(cancellationToken);
        var average = submittedCount == 0 ? null : await submitted.AverageAsync(s => (decimal?)s.TotalScore, cancellationToken);

        var versions = await (from submission in submitted
                              join version in db.ApplicationVersions.AsNoTracking() on submission.ApplicationVersionId equals version.Id
                              group new { submission, version } by new { version.Id, version.VersionNumber, version.DisplayName } into grouped
                              orderby grouped.Key.VersionNumber, grouped.Key.Id
                              select new
                              {
                                  ApplicationVersionId = grouped.Key.Id,
                                  grouped.Key.VersionNumber,
                                  grouped.Key.DisplayName,
                                  SubmissionCount = grouped.Count(),
                                  AverageTotalScore = grouped.Average(x => (decimal)x.submission.TotalScore)
                              }).ToListAsync(cancellationToken);

        var versionIds = versions.Select(version => version.ApplicationVersionId).ToArray();
        var scoreCounts = versionIds.Length == 0
            ? new List<ScoreBucketRow>()
            : await submitted.Where(submission => versionIds.Contains(submission.ApplicationVersionId))
                .GroupBy(submission => new { submission.ApplicationVersionId, submission.TotalScore })
                .Select(group => new ScoreBucketRow(group.Key.ApplicationVersionId, group.Key.TotalScore, group.Count()))
                .ToListAsync(cancellationToken);
        var distributions = versions.Select(version => new VersionScoreDistribution(
            version.ApplicationVersionId,
            version.VersionNumber,
            version.DisplayName,
            version.SubmissionCount,
            version.AverageTotalScore,
            scoreCounts.Where(score => score.ApplicationVersionId == version.ApplicationVersionId)
                .OrderBy(score => score.TotalScore)
                .Select(score => new ScoreCount(score.TotalScore, score.Count))
                .ToArray())).ToArray();

        var applications = await db.Set<ApplicationEntity>().AsNoTracking()
            .OrderBy(application => application.Name)
            .Select(application => new ApplicationRow(application.Id, application.Name))
            .ToListAsync(cancellationToken);
        var applicationIds = applications.Select(application => application.Id).ToArray();
        var applicationVersions = applicationIds.Length == 0
            ? new List<ApplicationVersionRow>()
            : await db.ApplicationVersions.AsNoTracking()
                .Where(version => applicationIds.Contains(version.ApplicationId))
                .OrderBy(version => version.VersionNumber)
                .Select(version => new ApplicationVersionRow(version.ApplicationId, version.Id, version.VersionNumber, version.DisplayName))
                .ToListAsync(cancellationToken);
        var applicationOptions = applications.Select(application => new ReportApplicationOption(
            application.Id,
            application.Name,
            applicationVersions.Where(version => version.ApplicationId == application.Id)
                .Select(version => new ReportVersionOption(version.Id, version.VersionNumber, version.DisplayName))
                .ToArray())).ToArray();

        var drafts = db.ResponseDrafts.AsNoTracking().Where(response => response.Status == Domain.Enums.ResponseStatus.InProgress);
        if (filter.ApplicationId is int appId) drafts = drafts.Where(response => response.ApplicationId == appId);
        if (filter.VersionId is int versionId) drafts = drafts.Where(response => response.ApplicationVersionId == versionId);
        if (filter.FromUtc is DateTime from) drafts = drafts.Where(response => response.UpdatedUtc >= from);
        if (filter.ToUtcExclusive is DateTime to) drafts = drafts.Where(response => response.UpdatedUtc < to);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            drafts = drafts.Where(response => response.OwnerKey.Contains(search));
        }
        var inProgressCount = await drafts.CountAsync(cancellationToken);

        return new ReportOverview(submittedCount, inProgressCount, average, applicationOptions, distributions);
    }

    public async Task<SubmissionPage> GetPageAsync(ReportFilter filter, SubmissionCursor? cursor = null, int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        ValidateFilter(filter);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (cursor is not null && (cursor.Id < 1 || cursor.SubmittedUtc.Kind != DateTimeKind.Utc))
            throw new ArgumentException("The submission cursor requires a positive ID and UTC timestamp.", nameof(cursor));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var submissions = ApplySubmissionFilter(db.Submissions.AsNoTracking(), filter);
        if (cursor is not null)
            submissions = submissions.Where(submission => submission.SubmittedUtc < cursor.SubmittedUtc ||
                submission.SubmittedUtc == cursor.SubmittedUtc && submission.Id < cursor.Id);

        var rows = await (from submission in submissions
                          join application in db.Set<ApplicationEntity>().AsNoTracking() on submission.ApplicationId equals application.Id
                          join version in db.ApplicationVersions.AsNoTracking() on submission.ApplicationVersionId equals version.Id
                          orderby submission.SubmittedUtc descending, submission.Id descending
                          select new SubmissionListItem(submission.Id, application.Name, version.Id, version.VersionNumber,
                              version.DisplayName, submission.SubmittedUtc, submission.UserId, submission.TotalScore))
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var items = rows.Take(pageSize)
            .Select(item => item with { SubmittedUtc = DateTime.SpecifyKind(item.SubmittedUtc, DateTimeKind.Utc) })
            .ToArray();
        var nextCursor = hasMore && items.Length > 0
            ? new SubmissionCursor(items[^1].SubmittedUtc, items[^1].Id)
            : null;
        return new SubmissionPage(items, nextCursor, pageSize);
    }

    public async Task<SubmissionDetail?> GetDetailAsync(int submissionId, CancellationToken cancellationToken = default)
    {
        if (submissionId < 1) return null;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var header = await (from submission in db.Submissions.AsNoTracking()
                            join application in db.Set<ApplicationEntity>().AsNoTracking() on submission.ApplicationId equals application.Id
                            join version in db.ApplicationVersions.AsNoTracking() on submission.ApplicationVersionId equals version.Id
                            where submission.Id == submissionId
                            select new SubmissionHeader(submission.Id, submission.ApplicationId, application.Name,
                                version.Id, version.VersionNumber, version.DisplayName, version.ResultVisibility,
                                submission.SubmittedUtc, submission.UserId, submission.TotalScore))
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null) return null;

        var versionGraph = await db.ApplicationVersions.AsNoTracking()
            .Where(version => version.Id == header.ApplicationVersionId)
            .Include(version => version.Categories).ThenInclude(category => category.Elements)
                .ThenInclude(element => element.Question).ThenInclude(question => question!.Options)
            .Include(version => version.Categories).ThenInclude(category => category.Elements)
                .ThenInclude(element => element.Question).ThenInclude(question => question!.Rules)
            .SingleAsync(cancellationToken);
        var questionStates = await db.SubmissionQuestionStates.AsNoTracking()
            .Where(state => state.SubmissionId == submissionId)
            .ToDictionaryAsync(state => state.QuestionId, cancellationToken);
        var answers = await db.Answers.AsNoTracking()
            .Where(answer => answer.SubmissionId == submissionId)
            .Select(answer => new AnswerRow(answer.Id, answer.QuestionId, answer.TextValue, answer.NumberValue, answer.PointsEarned))
            .ToListAsync(cancellationToken);
        var answerIds = answers.Select(answer => answer.Id).ToArray();
        var selectedOptionRows = answerIds.Length == 0
            ? new List<SelectedOptionRow>()
            : await db.AnswerSelectedOptions.AsNoTracking().Where(option => answerIds.Contains(option.AnswerId))
                .Select(option => new SelectedOptionRow(option.AnswerId, option.QuestionOptionId))
                .ToListAsync(cancellationToken);
        var optionLabels = versionGraph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null)
            .SelectMany(element => element.Question!.Options)
            .ToDictionary(option => option.Id, option => option.Label);
        var answersByQuestion = answers.ToDictionary(answer => answer.QuestionId);
        var optionsByAnswer = selectedOptionRows.GroupBy(option => option.AnswerId)
            .ToDictionary(group => group.Key, group => group.Select(option => option.QuestionOptionId).ToArray());
        var categoryScores = await db.SubmissionCategoryScores.AsNoTracking()
            .Where(score => score.SubmissionId == submissionId)
            .ToDictionaryAsync(score => score.CategoryId, score => score.Score, cancellationToken);
        var ruleRows = await db.SubmissionRuleTraces.AsNoTracking()
            .Where(trace => trace.SubmissionId == submissionId)
            .Select(trace => new RuleTraceRow(trace.QuestionId, trace.RuleId, trace.Matches, trace.Contributes, trace.Points))
            .ToListAsync(cancellationToken);
        var questions = versionGraph.Categories.OrderBy(category => category.SortOrder)
            .SelectMany(category => category.Elements.OrderBy(element => element.SortOrder))
            .Where(element => element.Question is not null)
            .Select(element =>
            {
                var question = element.Question!;
                questionStates.TryGetValue(question.Id, out var state);
                answersByQuestion.TryGetValue(question.Id, out var answer);
                var applicable = state?.IsApplicable;
                var showAnswer = applicable != false;
                var selectedIds = showAnswer && answer is not null && optionsByAnswer.TryGetValue(answer.Id, out var ids)
                    ? ids : Array.Empty<int>();
                return new SubmissionQuestionDetail(question.Id, state?.QuestionLogicalKey ?? element.LogicalKey,
                    element.Label, question.AnswerType.ToString(), applicable, showAnswer && answer is not null,
                    showAnswer ? answer?.TextValue : null, showAnswer ? answer?.NumberValue : null,
                    selectedIds.Where(optionLabels.ContainsKey).Select(id => optionLabels[id]).ToArray(),
                    state?.PointsEarned ?? (showAnswer ? answer?.PointsEarned ?? 0 : 0));
            }).ToArray();
        var ruleMap = versionGraph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null)
            .SelectMany(element => element.Question!.Rules.Select(rule => (element.Question!.Id, Rule: rule, element.Label)))
            .ToDictionary(x => x.Rule.Id);
        var traces = ruleRows.Select(trace => ruleMap.TryGetValue(trace.RuleId, out var rule)
            ? new SubmissionRuleDetail(trace.QuestionId, rule.Label, trace.RuleId, rule.Rule.SortOrder,
                trace.Matches, trace.Contributes, trace.Points)
            : new SubmissionRuleDetail(trace.QuestionId, "Unknown question", trace.RuleId, int.MaxValue,
                trace.Matches, trace.Contributes, trace.Points))
            .OrderBy(trace => trace.QuestionId).ThenBy(trace => trace.SortOrder).ToArray();
        var categories = versionGraph.Categories.OrderBy(category => category.SortOrder)
            .Where(category => categoryScores.ContainsKey(category.Id))
            .Select(category => new CategoryScoreDto(category.Name, category.SortOrder, categoryScores[category.Id]))
            .ToArray();

        return new SubmissionDetail(header.Id, header.ApplicationId, header.ApplicationName,
            header.ApplicationVersionId, header.VersionNumber, header.DisplayName,
            DateTime.SpecifyKind(header.SubmittedUtc, DateTimeKind.Utc),
            header.OwnerReference, header.TotalScore, header.ResultVisibility, categories, questions, traces);
    }

    private static IQueryable<Submission> ApplySubmissionFilter(IQueryable<Submission> query, ReportFilter filter)
    {
        if (filter.ApplicationId is int applicationId) query = query.Where(submission => submission.ApplicationId == applicationId);
        if (filter.VersionId is int versionId) query = query.Where(submission => submission.ApplicationVersionId == versionId);
        if (filter.FromUtc is DateTime fromUtc) query = query.Where(submission => submission.SubmittedUtc >= fromUtc);
        if (filter.ToUtcExclusive is DateTime toUtc) query = query.Where(submission => submission.SubmittedUtc < toUtc);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(submission => submission.UserId != null && submission.UserId.Contains(search));
        }
        return query;
    }

    private static void ValidateFilter(ReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.ApplicationId is <= 0) throw new ArgumentOutOfRangeException(nameof(filter.ApplicationId));
        if (filter.VersionId is <= 0) throw new ArgumentOutOfRangeException(nameof(filter.VersionId));
        if (filter.FromUtc is DateTime from && from.Kind != DateTimeKind.Utc)
            throw new ArgumentException("FromUtc must have DateTimeKind.Utc.", nameof(filter));
        if (filter.ToUtcExclusive is DateTime to && to.Kind != DateTimeKind.Utc)
            throw new ArgumentException("ToUtcExclusive must have DateTimeKind.Utc.", nameof(filter));
        if (filter.FromUtc is DateTime fromUtc && filter.ToUtcExclusive is DateTime toUtc && fromUtc >= toUtc)
            throw new ArgumentException("FromUtc must be earlier than ToUtcExclusive.", nameof(filter));
        if (filter.Search?.Trim().Length > MaximumSearchLength)
            throw new ArgumentException($"Search must not exceed {MaximumSearchLength} characters.", nameof(filter));
    }

    private sealed record ApplicationRow(int Id, string Name);
    private sealed record ApplicationVersionRow(int ApplicationId, int Id, int VersionNumber, string DisplayName);
    private sealed record ScoreBucketRow(int ApplicationVersionId, int TotalScore, int Count);
    private sealed record SubmissionHeader(int Id, int ApplicationId, string ApplicationName, int ApplicationVersionId,
        int VersionNumber, string DisplayName, Domain.Enums.ResultVisibility ResultVisibility, DateTime SubmittedUtc,
        string? OwnerReference, int TotalScore);
    private sealed record AnswerRow(int Id, int QuestionId, string? TextValue, decimal? NumberValue, int PointsEarned);
    private sealed record SelectedOptionRow(int AnswerId, int QuestionOptionId);
    private sealed record RuleTraceRow(int QuestionId, int RuleId, bool Matches, bool Contributes, int Points);
}
