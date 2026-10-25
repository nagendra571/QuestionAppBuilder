using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Application.Composition;

namespace QuestionAppBuilder.Application.Evaluation;

public sealed record EvaluationResult(
    IReadOnlySet<int> VisibleCategoryIds,
    IReadOnlySet<int> VisibleQuestionIds,
    IReadOnlyList<DefinitionIssue> DefinitionIssues,
    IReadOnlyList<DefinitionIssue> AnswerIssues,
    SubmissionScore Score)
{
    public QuestionAppBuilder.Application.Composition.CompositionResult? Composition { get; init; }
    /// <summary>Visible elements for current evaluations; null means a legacy/custom evaluator supplied no element-level result.</summary>
    public IReadOnlySet<int>? VisibleElementIds { get; init; }
    /// <summary>Authoring-preview explanations for hidden elements; null means the evaluator did not provide a trace.</summary>
    public IReadOnlyList<HiddenElementReason>? HiddenElementReasons { get; init; }
    /// <summary>Preview-only trace of failed combined validations; null means evaluation could not provide a complete trace.</summary>
    public IReadOnlyList<CompositionValidationFailure>? FailedValidationRules { get; init; }
}

public sealed record HiddenElementReason(int ElementId, int CategoryId, string Code, int? ConditionId = null,
    int? ParentElementId = null, int? ParentOptionId = null);
