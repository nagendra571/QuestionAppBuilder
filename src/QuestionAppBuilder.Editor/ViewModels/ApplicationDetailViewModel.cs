using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Application.Authoring;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class ApplicationDetailViewModel
{
    public required ApplicationEntity Application { get; init; }

    public ApplicationVersionGraph? DraftGraph { get; init; }

    public ApplicationVersionGraph? PublishedGraph { get; init; }

    public bool IsEditable => DraftGraph is not null;

    public string TypeName { get; init; } = string.Empty;

    public PublicationReview? DraftReview { get; init; }
}
