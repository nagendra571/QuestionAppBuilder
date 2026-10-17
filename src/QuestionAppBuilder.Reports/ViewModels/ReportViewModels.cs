using QuestionAppBuilder.Application.Reporting;

namespace QuestionAppBuilder.Reports.ViewModels;

public sealed record ReportFilterViewModel(int? ApplicationId, int? VersionId, DateOnly? FromDate, DateOnly? ThroughDate, string? Search);
public sealed record ReportIndexViewModel(ReportOverview Overview, ReportFilterViewModel Filter);
public sealed record ReportSubmissionsViewModel(SubmissionPage Page, ReportFilterViewModel Filter);
