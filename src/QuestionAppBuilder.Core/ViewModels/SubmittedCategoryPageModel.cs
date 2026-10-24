using QuestionAppBuilder.Application.Responses;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record SubmittedCategoryPageModel(SubmittedCategory Category, Guid ResponseId, bool ShowScores);
