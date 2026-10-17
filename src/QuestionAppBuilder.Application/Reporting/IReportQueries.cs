namespace QuestionAppBuilder.Application.Reporting;

public interface IReportQueries
{
    Task<ReportOverview> GetOverviewAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<SubmissionPage> GetPageAsync(ReportFilter filter, SubmissionCursor? cursor = null, int pageSize = 25,
        CancellationToken cancellationToken = default);
    Task<SubmissionDetail?> GetDetailAsync(int submissionId, CancellationToken cancellationToken = default);
}
