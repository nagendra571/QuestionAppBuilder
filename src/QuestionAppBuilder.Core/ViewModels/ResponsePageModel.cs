using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Core.Rendering;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record ResponsePageModel(ResponseSnapshot Response, QuestionnaireRenderModel Questionnaire,
    int CurrentCategoryIndex, string LayoutName, IReadOnlyDictionary<int, string>? FieldErrors = null,
    string? SummaryMessage = null, string? SaveStatus = null, string? RequestId = null,
    IReadOnlyDictionary<int, AnswerFormInput>? InputValues = null, string? RejectedExpectedRevision = null)
{
    public string ExpectedRevision => RejectedExpectedRevision ?? Convert.ToBase64String(Response.Revision);
}

public sealed class ResponseFormInput
{
    public string? ExpectedRevision { get; set; }
    public int CurrentCategoryIndex { get; set; }
    public int TargetCategoryIndex { get; set; }
    public string? Intent { get; set; }
    public string? RequestId { get; set; }
    public Dictionary<int, AnswerFormInput> Answers { get; set; } = new();
}

public sealed class AnswerFormInput
{
    public string? Text { get; set; }
    public string? Number { get; set; }
    public List<int>? SelectedOptionIds { get; set; }
}
