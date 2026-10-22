using QuestionAppBuilder.Application.Scoring;

namespace QuestionAppBuilder.Application.Evaluation;

public sealed record EvaluationResult(
    IReadOnlySet<int> VisibleCategoryIds,
    IReadOnlySet<int> VisibleQuestionIds,
    IReadOnlyList<DefinitionIssue> DefinitionIssues,
    IReadOnlyList<DefinitionIssue> AnswerIssues,
    SubmissionScore Score)
{
    public QuestionAppBuilder.Application.Composition.CompositionResult? Composition { get; init; }
}
