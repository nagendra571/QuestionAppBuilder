using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Controllers;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class DraftDeletionControllerTests
{
    [Fact]
    public async Task Preview_confirmation_token_binds_draft_target_and_revision_for_confirm()
    {
        var deletions = new FakeDeletionCoordinator();
        var controller = new DeletionsController(new QuestionAppBuilderEditorOptions(), deletions, new EphemeralDataProtectionProvider());
        var revision = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        deletions.Preview = new DraftDeletionPreview(73, 91, DraftDeletionKind.Option, 17, Guid.NewGuid(), revision,
            "Premium", [], [], [], [], []);

        var result = await controller.Preview(73, DraftDeletionKind.Option, 17, Convert.ToBase64String(revision), CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Preview");
        var model = view.Model.Should().BeOfType<QuestionAppBuilder.Editor.ViewModels.DeletionPreviewViewModel>().Subject;
        model.Preview.TargetName.Should().Be("Premium");
        var confirmed = await controller.Confirm(model.ConfirmationToken, CancellationToken.None);
        confirmed.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Detail");
        deletions.Confirmed.Should().NotBeNull();
        deletions.Confirmed!.ApplicationId.Should().Be(73);
        deletions.Confirmed.ExpectedVersionId.Should().Be(91);
        deletions.Confirmed.Kind.Should().Be(DraftDeletionKind.Option);
        deletions.Confirmed.TargetId.Should().Be(17);
        deletions.Confirmed.TargetLogicalKey.Should().Be(deletions.Preview.TargetLogicalKey);
        deletions.Confirmed.ExpectedRevision.Should().Equal(revision);
    }

    [Fact]
    public async Task Preview_rejects_invalid_revision_and_confirm_rejects_tampered_token()
    {
        var deletions = new FakeDeletionCoordinator();
        var controller = new DeletionsController(new QuestionAppBuilderEditorOptions(), deletions, new EphemeralDataProtectionProvider());

        (await controller.Preview(73, DraftDeletionKind.Category, 1, "broken", CancellationToken.None)).Should().BeOfType<BadRequestResult>();
        (await controller.Confirm("tampered", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        deletions.PreviewCalls.Should().Be(0);
        deletions.Confirmed.Should().BeNull();
    }

    [Fact]
    public async Task Stale_preview_returns_to_detail_with_actionable_message()
    {
        var deletions = new FakeDeletionCoordinator { ThrowConflict = true };
        var controller = new DeletionsController(new QuestionAppBuilderEditorOptions(), deletions, new EphemeralDataProtectionProvider());
        controller.TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider());

        var result = await controller.Preview(73, DraftDeletionKind.Element, 2,
            Convert.ToBase64String(new byte[16]), CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Detail");
        controller.TempData["EditorError"].Should().NotBeNull();
    }

    private sealed class FakeDeletionCoordinator : IDraftDeletionCoordinator
    {
        public DraftDeletionPreview Preview { get; set; } = new(73, 91, DraftDeletionKind.Category, 1, Guid.NewGuid(),
            new byte[16], "Intro", [], [], [], [], []);
        public int PreviewCalls { get; private set; }
        public bool ThrowConflict { get; init; }
        public ConfirmDraftDeletionRequest? Confirmed { get; private set; }
        public Task<DraftDeletionPreview> PreviewAsync(DraftDeletionTarget target, byte[] expectedRevision, CancellationToken ct = default)
        {
            PreviewCalls++;
            if (ThrowConflict) throw new DraftConflictException();
            return Task.FromResult(Preview);
        }
        public Task ConfirmAsync(ConfirmDraftDeletionRequest request, CancellationToken ct = default)
        { Confirmed = request; return Task.CompletedTask; }
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
