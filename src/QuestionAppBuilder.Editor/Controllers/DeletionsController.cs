using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class DeletionsController : EditorControllerBase
{
    private readonly IDraftDeletionCoordinator _deletions;
    private readonly IDataProtector _protector;

    public DeletionsController(QuestionAppBuilderEditorOptions options, IDraftDeletionCoordinator deletions,
        IDataProtectionProvider dataProtection) : base(options)
    {
        _deletions = deletions;
        _protector = dataProtection.CreateProtector("QuestionAppBuilder.DraftDeletionPreview.v1");
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Preview(int applicationId, DraftDeletionKind kind, int targetId, string? expectedRevision,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision)) return BadRequest();
        byte[] revision;
        try { revision = Convert.FromBase64String(expectedRevision); }
        catch (FormatException) { return BadRequest(); }
        if (!Enum.IsDefined(kind)) return BadRequest();
        try
        {
            var preview = await _deletions.PreviewAsync(new DraftDeletionTarget(applicationId, kind, targetId), revision, ct);
            var claims = new DeletionToken(preview.ApplicationId, preview.VersionId, preview.Kind, preview.TargetId,
                preview.TargetLogicalKey, Convert.ToBase64String(preview.Revision));
            return View("Preview", new DeletionPreviewViewModel(preview, _protector.Protect(JsonSerializer.Serialize(claims))));
        }
        catch (DraftConflictException)
        {
            SetEditorError("The draft changed. Review the latest draft and reopen the deletion preview.");
            return RedirectToAction("Detail", "Applications", new { id = applicationId });
        }
        catch (NotFoundException)
        {
            SetEditorError("The draft or selected item is no longer available. Review the latest version.");
            return RedirectToAction("Detail", "Applications", new { id = applicationId });
        }
        catch (KeyNotFoundException)
        {
            SetEditorError("The selected item is not in the current draft.");
            return RedirectToAction("Detail", "Applications", new { id = applicationId });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string? confirmationToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(confirmationToken)) return BadRequest();
        DeletionToken? claims;
        try { claims = JsonSerializer.Deserialize<DeletionToken>(_protector.Unprotect(confirmationToken)); }
        catch (CryptographicException) { return BadRequest(new { message = "This deletion confirmation is invalid. Reopen the preview." }); }
        catch (JsonException) { return BadRequest(new { message = "This deletion confirmation is invalid. Reopen the preview." }); }
        if (claims is null || claims.ApplicationId <= 0 || claims.VersionId <= 0 || claims.TargetId <= 0
            || !Enum.IsDefined(claims.Kind) || claims.TargetLogicalKey == Guid.Empty) return BadRequest();
        byte[] revision;
        try { revision = Convert.FromBase64String(claims.ExpectedRevision); }
        catch (FormatException) { return BadRequest(); }
        if (revision.Length != 16) return BadRequest();
        try
        {
            await _deletions.ConfirmAsync(new ConfirmDraftDeletionRequest(claims.ApplicationId, claims.VersionId,
                claims.Kind, claims.TargetId, claims.TargetLogicalKey, revision), ct);
            return RedirectToAction("Detail", "Applications", new { id = claims.ApplicationId });
        }
        catch (DraftConflictException)
        {
            SetEditorError("The draft changed after this preview. Reopen the deletion preview from the latest draft.");
            return RedirectToAction("Detail", "Applications", new { id = claims.ApplicationId });
        }
        catch (NotFoundException)
        {
            SetEditorError("The draft or selected item is no longer available. Review the latest version.");
            return RedirectToAction("Detail", "Applications", new { id = claims.ApplicationId });
        }
        catch (DefinitionValidationException exception)
        {
            SetEditorError(exception.Message);
            return RedirectToAction("Detail", "Applications", new { id = claims.ApplicationId });
        }
    }

    private sealed record DeletionToken(int ApplicationId, int VersionId, DraftDeletionKind Kind, int TargetId,
        Guid TargetLogicalKey, string ExpectedRevision);
}
