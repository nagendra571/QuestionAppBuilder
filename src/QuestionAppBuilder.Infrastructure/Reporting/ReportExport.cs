using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Infrastructure.Persistence;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Reporting;

public sealed class ReportExport : IReportExport
{
    private const int BatchSize = 100;
    private const int MaximumSearchLength = 128;
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;

    public ReportExport(IDbContextFactory<QuestionAppBuilderDbContext> factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task WriteAsync(ExportFilter export, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(export.Filter);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFilter(export.Filter);

        ApplicationVersion? definition = null;
        if (export.IncludeAnswers)
        {
            if (export.Filter.ApplicationId is not int applicationId || export.Filter.VersionId is not int versionId)
                throw new ArgumentException("Answer exports require both ApplicationId and VersionId because answers are pinned to one version.", nameof(export));
            await using var definitionContext = await _factory.CreateDbContextAsync(cancellationToken);
            definition = await definitionContext.ApplicationVersions.AsNoTracking()
                .Where(version => version.Id == versionId && version.ApplicationId == applicationId)
                .Include(version => version.Categories).ThenInclude(category => category.Elements)
                    .ThenInclude(element => element.Question).ThenInclude(question => question!.Options)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException("The selected version does not belong to the selected application.", nameof(export));
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024,
            leaveOpen: true);
        await WriteRowAsync(writer, export.IncludeAnswers
            ? new[] { "Submission ID", "Application ID", "Application", "Version ID", "Version", "Submitted UTC", "Owner Reference", "Question Key", "Question", "Applicability", "Answer Status", "Answer", "Points" }
            : new[] { "Submission ID", "Application ID", "Application", "Version ID", "Version", "Submitted UTC", "Owner Reference", "Total Score" }, cancellationToken);

        var questions = definition?.Categories.OrderBy(category => category.SortOrder)
            .SelectMany(category => category.Elements.OrderBy(element => element.SortOrder))
            .Where(element => element.Question is not null)
            .Select(element => new ExportQuestion(element.LogicalKey, element.Label, element.Question!.Id,
                element.Question.Options.ToDictionary(option => option.Id, option => option.Label)))
            .ToArray() ?? Array.Empty<ExportQuestion>();

        DateTime? afterSubmittedUtc = null;
        var afterId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = ApplySubmissionFilter(db.Submissions.AsNoTracking(), export.Filter);
            if (afterSubmittedUtc is DateTime timestamp)
                query = query.Where(submission => submission.SubmittedUtc > timestamp ||
                    submission.SubmittedUtc == timestamp && submission.Id > afterId);
            var batch = await (from submission in query
                               join application in db.Set<ApplicationEntity>().AsNoTracking() on submission.ApplicationId equals application.Id
                               join version in db.ApplicationVersions.AsNoTracking() on submission.ApplicationVersionId equals version.Id
                               orderby submission.SubmittedUtc, submission.Id
                               select new ExportSubmission(submission.Id, submission.ApplicationId, application.Name,
                                   version.Id, version.VersionNumber, version.DisplayName, submission.SubmittedUtc,
                                   submission.UserId, submission.TotalScore))
                .Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            if (!export.IncludeAnswers)
            {
                foreach (var submission in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteRowAsync(writer, new[]
                    {
                        submission.Id.ToString(CultureInfo.InvariantCulture),
                        submission.ApplicationId.ToString(CultureInfo.InvariantCulture), submission.ApplicationName,
                        submission.VersionId.ToString(CultureInfo.InvariantCulture),
                        $"v{submission.VersionNumber} {submission.DisplayName}", FormatUtc(submission.SubmittedUtc),
                        submission.OwnerReference ?? string.Empty, submission.TotalScore.ToString(CultureInfo.InvariantCulture)
                    }, cancellationToken);
                }
            }
            else
            {
                await WriteAnswerBatchAsync(db, writer, batch, questions, cancellationToken);
            }

            var last = batch[^1];
            afterSubmittedUtc = last.SubmittedUtc;
            afterId = last.Id;
            await writer.FlushAsync(cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task WriteAnswerBatchAsync(QuestionAppBuilderDbContext db, StreamWriter writer,
        IReadOnlyList<ExportSubmission> batch, IReadOnlyList<ExportQuestion> questions, CancellationToken cancellationToken)
    {
        if (questions.Count == 0) return;
        var ids = batch.Select(submission => submission.Id).ToArray();
        var states = await db.SubmissionQuestionStates.AsNoTracking().Where(state => ids.Contains(state.SubmissionId))
            .Select(state => new StateRow(state.SubmissionId, state.QuestionId, state.QuestionLogicalKey, state.IsApplicable, state.PointsEarned))
            .ToListAsync(cancellationToken);
        var stateMap = states.ToDictionary(state => (state.SubmissionId, state.QuestionId));
        var answers = await db.Answers.AsNoTracking().Where(answer => ids.Contains(answer.SubmissionId))
            .Select(answer => new AnswerRow(answer.Id, answer.SubmissionId, answer.QuestionId,
                answer.TextValue, answer.NumberValue, answer.PointsEarned))
            .ToListAsync(cancellationToken);
        var answerMap = answers.ToDictionary(answer => (answer.SubmissionId, answer.QuestionId));
        var answerIds = answers.Select(answer => answer.Id).ToArray();
        var selected = answerIds.Length == 0
            ? new List<SelectedOptionRow>()
            : await db.AnswerSelectedOptions.AsNoTracking().Where(option => answerIds.Contains(option.AnswerId))
                .Select(option => new SelectedOptionRow(option.AnswerId, option.QuestionOptionId))
                .ToListAsync(cancellationToken);
        var selectedMap = selected.GroupBy(option => option.AnswerId)
            .ToDictionary(group => group.Key, group => group.Select(option => option.QuestionOptionId).ToArray());

        foreach (var submission in batch)
        foreach (var question in questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stateMap.TryGetValue((submission.Id, question.QuestionId), out var state);
            answerMap.TryGetValue((submission.Id, question.QuestionId), out var answer);
            var applicability = state is null ? "not-recorded" : state.IsApplicable ? "applicable" : "not-applicable";
            var showAnswer = state?.IsApplicable != false;
            var optionIds = showAnswer && answer is not null && selectedMap.TryGetValue(answer.Id, out var found)
                ? found : Array.Empty<int>();
            var answerText = !showAnswer || answer is null ? string.Empty
                : optionIds.Length > 0
                    ? string.Join("; ", optionIds.Where(question.OptionLabels.ContainsKey).Select(id => question.OptionLabels[id]))
                    : answer.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? answer.TextValue ?? string.Empty;
            var answerStatus = !showAnswer ? "not-applicable" : answer is null ? "unanswered" : "answered";
            await WriteRowAsync(writer, new[]
            {
                submission.Id.ToString(CultureInfo.InvariantCulture),
                submission.ApplicationId.ToString(CultureInfo.InvariantCulture), submission.ApplicationName,
                submission.VersionId.ToString(CultureInfo.InvariantCulture),
                $"v{submission.VersionNumber} {submission.DisplayName}", FormatUtc(submission.SubmittedUtc),
                submission.OwnerReference ?? string.Empty,
                (state?.QuestionLogicalKey ?? question.LogicalKey).ToString("D"), question.Label, applicability,
                answerStatus, answerText,
                (state?.PointsEarned ?? (showAnswer ? answer?.PointsEarned ?? 0 : 0)).ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
        }
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

    private static async Task WriteRowAsync(StreamWriter writer, IEnumerable<string> cells, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = string.Join(',', cells.Select(EncodeCell));
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    private static string EncodeCell(string value)
    {
        var prefixIndex = 0;
        while (prefixIndex < value.Length && char.IsWhiteSpace(value[prefixIndex]) && value[prefixIndex] is not ('\t' or '\r' or '\n'))
            prefixIndex++;
        if (prefixIndex < value.Length && value[prefixIndex] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            value = "'" + value;
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return value;
    }

    private static string FormatUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        .ToString("O", CultureInfo.InvariantCulture);

    private sealed record ExportSubmission(int Id, int ApplicationId, string ApplicationName, int VersionId,
        int VersionNumber, string DisplayName, DateTime SubmittedUtc, string? OwnerReference, int TotalScore);
    private sealed record ExportQuestion(Guid LogicalKey, string Label, int QuestionId, Dictionary<int, string> OptionLabels);
    private sealed record StateRow(int SubmissionId, int QuestionId, Guid QuestionLogicalKey, bool IsApplicable, int PointsEarned);
    private sealed record AnswerRow(int Id, int SubmissionId, int QuestionId, string? TextValue, decimal? NumberValue, int PointsEarned);
    private sealed record SelectedOptionRow(int AnswerId, int QuestionOptionId);
}
