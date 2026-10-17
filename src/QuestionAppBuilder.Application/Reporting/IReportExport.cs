namespace QuestionAppBuilder.Application.Reporting;

public interface IReportExport
{
    Task WriteAsync(ExportFilter filter, Stream destination, CancellationToken cancellationToken = default);
}
