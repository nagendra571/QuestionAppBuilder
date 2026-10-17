using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Reports.Options;
using QuestionAppBuilder.Reports.Time;

namespace QuestionAppBuilder.Reports.Controllers;

public sealed class ExportsController : ReportControllerBase
{
    private readonly IReportExport _exports;
    private readonly TimeZoneInfo _timeZone;

    public ExportsController(IReportExport exports, QuestionAppBuilderReportsOptions options)
    {
        _exports = exports;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.DisplayTimeZone);
    }

    [HttpGet]
    public IActionResult Export(int? applicationId, int? versionId, DateOnly? from, DateOnly? through,
        string? search, bool includeAnswers, CancellationToken cancellationToken)
    {
        if (applicationId is <= 0 || versionId is <= 0)
            return BadRequest("Application and version IDs must be positive.");
        if (search?.Trim().Length > 128)
            return BadRequest("Owner reference search must not exceed 128 characters.");
        if (includeAnswers && (applicationId is null || versionId is null))
            return BadRequest("Answer exports require both applicationId and versionId.");

        try
        {
            var (fromUtc, toUtcExclusive) = ReportDateRange.FromLocalDates(from, through, _timeZone);
            var filter = new ExportFilter(new ReportFilter(applicationId, versionId, fromUtc, toUtcExclusive, search), includeAnswers);
            return new StreamingCsvResult(_exports, filter, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
    }
}

public sealed class StreamingCsvResult : IActionResult
{
    private readonly IReportExport _export;
    private readonly ExportFilter _filter;
    private readonly CancellationToken _requestCancellation;

    public StreamingCsvResult(IReportExport export, ExportFilter filter, CancellationToken requestCancellation)
    {
        _export = export;
        _filter = filter;
        _requestCancellation = requestCancellation;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = "attachment; filename=\"questionappbuilder-submissions.csv\"";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.HttpContext.RequestAborted, _requestCancellation);
        try
        {
            await _export.WriteAsync(_filter, response.Body, linkedCancellation.Token);
        }
        catch (ArgumentException exception) when (!response.HasStarted)
        {
            response.Clear();
            response.StatusCode = StatusCodes.Status400BadRequest;
            response.ContentType = "text/plain; charset=utf-8";
            await response.WriteAsync(exception.Message, linkedCancellation.Token);
        }
    }
}
