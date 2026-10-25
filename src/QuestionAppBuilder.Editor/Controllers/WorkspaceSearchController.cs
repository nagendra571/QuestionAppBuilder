using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class WorkspaceSearchController : EditorControllerBase
{
    private static readonly Regex WorkspaceFragment = new("^(?:category|element|option|rule|condition)-[1-9][0-9]*$|^rules-[1-9][0-9]*$|^visibility$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GuidFragment = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> TemplateSlots = ["ElementLabel", "ElementHelpText", "ReviewSummary", "ConfirmationText"];
    private readonly IDraftService _drafts;

    public WorkspaceSearchController(QuestionAppBuilderEditorOptions options, IDraftService drafts) : base(options)
        => _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Search(int applicationId, string? q, string? expectedRevision, CancellationToken ct)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > DefinitionSearcher.MaximumQueryLength || string.IsNullOrWhiteSpace(expectedRevision))
            return BadRequest(new { message = $"Enter search text between 1 and {DefinitionSearcher.MaximumQueryLength} characters." });
        byte[] requestedRevision;
        try { requestedRevision = Convert.FromBase64String(expectedRevision); }
        catch (FormatException) { return BadRequest(new { message = "The draft revision is invalid. Save or reload the workspace, then search again." }); }
        if (requestedRevision.Length == 0) return BadRequest(new { message = "The draft revision is invalid. Save or reload the workspace, then search again." });

        try
        {
            var snapshot = await _drafts.GetAsync(applicationId, ct);
            if (!snapshot.Revision.AsSpan().SequenceEqual(requestedRevision))
                return Conflict(new { message = "The saved draft changed. Save or reload before searching again." });

            var report = DefinitionSearcher.Search(snapshot.Definition, query);
            var items = report.Items.Select(item => new
            {
                Kind = item.Kind.ToString(), item.Name, item.MatchedText,
                MatchKind = item.MatchKind.ToString(), item.ReferenceLabel, item.Context,
                Url = BuildDestinationUrl(applicationId, item)
            }).ToArray();
            return Json(new
            {
                report.VersionId,
                Revision = Convert.ToBase64String(snapshot.Revision),
                Items = items,
                report.TotalCount,
                report.Warnings,
                report.Complete
            });
        }
        catch (NotFoundException) { return NotFound(); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }

    private string? BuildDestinationUrl(int applicationId, DefinitionSearchItem item)
    {
        var destination = item.Destination;
        if (!IsAllowedFragment(destination))
        {
            var fragment = destination?.Kind == DefinitionSearchDestinationKind.Composition ? item.Kind switch
            {
                DefinitionSearchItemKind.Group => "Groups",
                DefinitionSearchItemKind.Calculation => "Calculations",
                DefinitionSearchItemKind.CombinedRule => "Rules",
                DefinitionSearchItemKind.DynamicVariable or DefinitionSearchItemKind.DynamicTemplate => "DynamicText",
                _ => "Groups"
            } : "content";
            destination = new DefinitionSearchDestination(destination?.Kind ?? DefinitionSearchDestinationKind.Workspace, fragment);
        }
        var baseUrl = destination.Kind switch
        {
            DefinitionSearchDestinationKind.Workspace => Url.Action("Index", "Drafts", new { applicationId }),
            DefinitionSearchDestinationKind.Composition => Url.Action("Index", "Composition", new { id = applicationId }),
            _ => null
        };
        return baseUrl is null ? null : $"{baseUrl}#{destination.Fragment}";
    }

    private static bool IsAllowedFragment(DefinitionSearchDestination destination)
    {
        if (destination is null || string.IsNullOrWhiteSpace(destination.Fragment)) return false;
        if (destination.Kind == DefinitionSearchDestinationKind.Workspace)
            return destination.Fragment is "content" or "definition" or "scoring" or "readiness" || WorkspaceFragment.IsMatch(destination.Fragment);
        if (destination.Kind != DefinitionSearchDestinationKind.Composition) return false;

        var segments = destination.Fragment.Split('/', StringSplitOptions.None);
        if (segments.Length == 1) return segments[0] is "Groups" or "Calculations" or "Rules" or "DynamicText";
        if (segments.Length == 2 && segments[0] is "Groups" or "Calculations" or "Rules")
            return GuidFragment.IsMatch(segments[1]);
        if (segments.Length == 3 && segments[0] == "DynamicText" && segments[1] == "Variables")
            return GuidFragment.IsMatch(segments[2]);
        if (segments.Length == 4 && segments[0] == "DynamicText" && segments[1] == "Templates")
            return TemplateSlots.Contains(segments[2]) && GuidFragment.IsMatch(segments[3]);
        return false;
    }
}
