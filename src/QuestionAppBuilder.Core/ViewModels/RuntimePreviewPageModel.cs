using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Core.Rendering;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record RuntimePreviewPageModel(QuestionnaireRenderModel Questionnaire, int CategoryIndex,
    NavigationMode NavigationMode, ResultVisibility ResultVisibility,
    IReadOnlyDictionary<int, string>? FieldErrors = null, string? SummaryMessage = null,
    bool IsReview = false, bool IsReceipt = false, SubmissionScore? Score = null,
    IReadOnlyList<DefinitionIssue>? DefinitionIssues = null);
