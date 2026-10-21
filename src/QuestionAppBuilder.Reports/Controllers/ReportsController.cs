using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Reports.Options;
using QuestionAppBuilder.Reports.Time;
using QuestionAppBuilder.Reports.ViewModels;

namespace QuestionAppBuilder.Reports.Controllers;

public sealed class ReportsController : ReportControllerBase
{
    private readonly IReportQueries _reports;
    private readonly QuestionAppBuilderReportsOptions _options;
    private readonly TimeZoneInfo _timeZone;

    public ReportsController(IReportQueries reports, QuestionAppBuilderReportsOptions options)
    {
        _reports = reports;
        _options = options;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.DisplayTimeZone);
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? applicationId, int? versionId, DateOnly? from, DateOnly? through,
        string? search, CancellationToken cancellationToken)
    {
        if (!TryBuildFilter(applicationId, versionId, from, through, search, out var filter)) return BadRequest(ModelState);
        ViewData["Title"] = "Submission analysis";
        ViewData["Layout"] = _options.LayoutName;
        var overview = await _reports.GetOverviewAsync(filter, cancellationToken);
        return View(new ReportIndexViewModel(overview, new(applicationId, versionId, from, through, search)));
    }

    [HttpGet]
    public async Task<IActionResult> Submissions(int? applicationId, int? versionId, DateOnly? from, DateOnly? through,
        string? search, string? afterUtc, int? afterId, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (!TryBuildFilter(applicationId, versionId, from, through, search, out var filter)) return BadRequest(ModelState);
        SubmissionCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(afterUtc) || afterId is not null)
        {
            if (string.IsNullOrWhiteSpace(afterUtc) || afterId is null ||
                !DateTime.TryParse(afterUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var submittedUtc) ||
                submittedUtc.Kind != DateTimeKind.Utc || afterId < 1)
                return BadRequest("The report cursor is invalid.");
            cursor = new SubmissionCursor(submittedUtc, afterId.Value);
        }

        var page = await _reports.GetPageAsync(filter, cursor, pageSize, cancellationToken);
        ViewData["Title"] = "Submissions";
        ViewData["Layout"] = _options.LayoutName;
        ViewData["NextUrl"] = page.NextCursor is null ? null : Url.Action(nameof(Submissions), new
        {
            area = "QuestionAppBuilderReports", applicationId, versionId, from, through, search, pageSize = page.PageSize,
            afterUtc = page.NextCursor.SubmittedUtc.ToString("O", CultureInfo.InvariantCulture), afterId = page.NextCursor.Id
        });
        return View(new ReportSubmissionsViewModel(page, new(applicationId, versionId, from, through, search)));
    }

    [HttpGet]
    public async Task<IActionResult> Detail(int id, CancellationToken cancellationToken)
    {
        var detail = await _reports.GetDetailAsync(id, cancellationToken);
        if (detail is null) return NotFound();
        ViewData["Title"] = $"Submission {detail.Id}";
        ViewData["Layout"] = _options.LayoutName;
        return View(detail);
    }

    [HttpGet]
    public async Task<IActionResult> Document(int id, int questionId,
        [FromServices] QuestionAppBuilder.Application.Responses.IResponseDocumentService documents, CancellationToken cancellationToken)
    {
        var document = await documents.DownloadSubmissionAsync(id, questionId, cancellationToken);
        if (document is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(document.Content, "application/octet-stream", document.FileName);
    }

    private bool TryBuildFilter(int? applicationId, int? versionId, DateOnly? from, DateOnly? through, string? search,
        out ReportFilter filter)
    {
        try
        {
            if (applicationId is <= 0) throw new ArgumentOutOfRangeException(nameof(applicationId), "Application ID must be positive.");
            if (versionId is <= 0) throw new ArgumentOutOfRangeException(nameof(versionId), "Version ID must be positive.");
            if (search?.Trim().Length > 128) throw new ArgumentException("Owner reference search must not exceed 128 characters.", nameof(search));
            var (fromUtc, toUtcExclusive) = ReportDateRange.FromLocalDates(from, through, _timeZone);
            filter = new ReportFilter(applicationId, versionId, fromUtc, toUtcExclusive, search);
            return true;
        }
        catch (ArgumentException exception)
        {
            filter = new ReportFilter();
            ModelState.AddModelError(string.Empty, exception.Message);
            return false;
        }
    }
}
