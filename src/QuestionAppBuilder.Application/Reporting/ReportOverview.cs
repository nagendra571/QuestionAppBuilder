namespace QuestionAppBuilder.Application.Reporting;

public sealed record ReportApplicationOption(int Id, string Name, IReadOnlyList<ReportVersionOption> Versions);
public sealed record ReportVersionOption(int Id, int VersionNumber, string DisplayName);
public sealed record ScoreCount(int TotalScore, int Count);
public sealed record VersionScoreDistribution(
    int ApplicationVersionId,
    int VersionNumber,
    string DisplayName,
    int SubmissionCount,
    decimal AverageTotalScore,
    IReadOnlyList<ScoreCount> ScoreCounts);

public sealed record ReportOverview(
    int SubmittedCount,
    int InProgressCount,
    decimal? AverageTotalScore,
    IReadOnlyList<ReportApplicationOption> Applications,
    IReadOnlyList<VersionScoreDistribution> Versions);
