using QuestionAppBuilder.Application.Authoring;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed record DeletionPreviewViewModel(DraftDeletionPreview Preview, string ConfirmationToken);
