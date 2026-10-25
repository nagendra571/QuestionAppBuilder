using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

public enum DraftDeletionKind { Category, Element, Option }

public sealed record DraftDeletionTarget(int ApplicationId, DraftDeletionKind Kind, int TargetId);

public sealed record ConfirmDraftDeletionRequest(
    int ApplicationId,
    int ExpectedVersionId,
    DraftDeletionKind Kind,
    int TargetId,
    Guid TargetLogicalKey,
    byte[] ExpectedRevision);

public sealed record DeletionImpactItem(
    string Name,
    string Detail,
    string? Section = null,
    int? CategoryId = null,
    int? ElementId = null,
    Guid? Key = null);

public sealed record DraftDeletionPreview(
    int ApplicationId,
    int VersionId,
    DraftDeletionKind Kind,
    int TargetId,
    Guid TargetLogicalKey,
    byte[] Revision,
    string TargetName,
    IReadOnlyList<DeletionImpactItem> Removed,
    IReadOnlyList<DeletionImpactItem> AutomaticallyChanged,
    IReadOnlyList<DeletionImpactItem> NeedsRepair,
    IReadOnlyList<DeletionImpactItem> Blocked,
    IReadOnlyList<string> Warnings)
{
    public bool CanDelete => Blocked.Count == 0 && Warnings.Count == 0;
}

public interface IDraftDeletionCoordinator
{
    Task<DraftDeletionPreview> PreviewAsync(DraftDeletionTarget target, byte[] expectedRevision, CancellationToken ct = default);
    Task ConfirmAsync(ConfirmDraftDeletionRequest request, CancellationToken ct = default);
}
