using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Core.Rendering;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record ReviewPageModel(ResponseSnapshot Response, QuestionnaireRenderModel Questionnaire, string LayoutName,
    IReadOnlyDictionary<int, string>? FieldErrors = null, string? SummaryMessage = null, string? RequestId = null)
{
    public string ExpectedRevision => Convert.ToBase64String(Response.Revision);
}
