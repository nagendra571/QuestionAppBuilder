namespace QuestionAppBuilder.Application.Reporting;

/// <summary>UTC, half-open report filters. Search is limited to the stored owner reference.</summary>
public sealed record ReportFilter(
    int? ApplicationId = null,
    int? VersionId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtcExclusive = null,
    string? Search = null);
