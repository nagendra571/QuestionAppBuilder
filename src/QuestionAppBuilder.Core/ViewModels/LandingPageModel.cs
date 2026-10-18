using QuestionAppBuilder.Application.Responses;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record LandingPageModel(ResponseLandingSnapshot? Landing, int ApplicationId, string LayoutName)
{
    public bool HasStartGrant { get; init; }
}
