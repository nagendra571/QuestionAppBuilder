using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed record DraftWorkspaceViewModel(int ApplicationId, byte[] Revision, QuestionAppBuilder.Domain.Graphs.ApplicationVersionGraph Graph, DraftWorkspaceInput Definition,
    IReadOnlyList<DraftReadinessItem> Errors, IReadOnlyList<DraftReadinessItem> Warnings, string LayoutName);

public sealed record DraftReadinessItem(string Code, int? ElementId, string Message);

public sealed class DraftWorkspaceInput
{
    public int ApplicationId { get; set; }
    public byte[] ExpectedRevision { get; set; } = Array.Empty<byte>();
    public DraftVersionInput Version { get; set; } = new();
    public List<DraftCategoryInput> Categories { get; set; } = new();
    public List<DraftElementInput> Elements { get; set; } = new();
    public List<DraftQuestionInput> Questions { get; set; } = new();
    public List<DraftOptionInput> Options { get; set; } = new();
    public List<DraftRuleInput> Rules { get; set; } = new();
    public List<DraftConditionInput> Conditions { get; set; } = new();
}

public sealed class DraftVersionInput
{
    public string DisplayName { get; set; } = string.Empty;
    public string SubmitLabel { get; set; } = string.Empty;
    public string? ConfirmationText { get; set; }
    public NavigationMode NavigationMode { get; set; }
    public RespondentAccessMode RespondentAccessMode { get; set; }
    public ResultVisibility ResultVisibility { get; set; }
    public SubmittedApplicationVisibility SubmittedApplicationVisibility { get; set; }
}
public sealed class DraftCategoryInput { public int Id { get; set; } public string Name { get; set; } = string.Empty; public int SortOrder { get; set; } public int? VisibilityConditionId { get; set; } }
public sealed class DraftElementInput { public int Id { get; set; } public string Label { get; set; } = string.Empty; public string? HelpText { get; set; } public int SortOrder { get; set; } public int? VisibilityConditionId { get; set; } }
public sealed class DraftQuestionInput
{
    public int Id { get; set; } public AnswerType AnswerType { get; set; } public bool IsRequired { get; set; } public ScoringMode ScoringMode { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(QuestionAppBuilder.Domain.ValidationLimits.RequiredMessageMaxLength)]
    public string? RequiredMessage { get; set; }
    public bool WholeNumbersOnly { get; set; }
    public int? MaxLength { get; set; } public decimal? MinValue { get; set; } public decimal? MaxValue { get; set; }
    public int? MinSelections { get; set; } public int? MaxSelections { get; set; }
}
public sealed class DraftOptionInput { public int Id { get; set; } public string Label { get; set; } = string.Empty; public string Value { get; set; } = string.Empty; public int SortOrder { get; set; } }
public sealed class DraftRuleInput
{
    public int Id { get; set; } public RuleFamily Family { get; set; } public int Points { get; set; } public int SortOrder { get; set; }
    public bool MinInclusive { get; set; } public bool MaxInclusive { get; set; } public MembershipOperator? MembershipOperator { get; set; }
    public decimal? MinValue { get; set; } public decimal? MaxValue { get; set; } public List<int> OptionIds { get; set; } = new();
}
public sealed class DraftConditionInput { public int Id { get; set; } public ConditionJunction Junction { get; set; } public List<DraftPredicateInput> Predicates { get; set; } = new(); }
public sealed class DraftPredicateInput
{
    public int Id { get; set; } public int SourceQuestionId { get; set; } public ConditionOperator Operator { get; set; }
    public decimal? NumberValue { get; set; } public List<int> OptionIds { get; set; } = new();
}

public sealed class PreviewAnswerInput { public string? Text { get; set; } public string? Number { get; set; } public List<int> SelectedOptionIds { get; set; } = new(); }
public sealed class PreviewRequest
{
    public Dictionary<int, PreviewAnswerInput> Answers { get; set; } = new();
    public string? ExpectedRevision { get; set; }
    public int CategoryIndex { get; set; }
    public int? TargetCategoryIndex { get; set; }
    public bool Review { get; set; }
    public bool Submit { get; set; }
}
public sealed record PreviewResponse(QuestionAppBuilder.Core.Rendering.QuestionnaireRenderModel Questionnaire,
    QuestionAppBuilder.Application.Scoring.SubmissionScore Score, IReadOnlyList<QuestionAppBuilder.Application.Evaluation.DefinitionIssue> AnswerIssues,
    IReadOnlyList<QuestionAppBuilder.Application.Evaluation.DefinitionIssue> DefinitionIssues)
{
    public string Html { get; init; } = string.Empty;
    public QuestionAppBuilder.Domain.Enums.NavigationMode NavigationMode { get; init; }
    public QuestionAppBuilder.Domain.Enums.ResultVisibility ResultVisibility { get; init; }
    public QuestionAppBuilder.Domain.Enums.SubmittedApplicationVisibility SubmittedApplicationVisibility { get; init; }
    public int CategoryIndex { get; init; }
    public bool IsReceipt { get; init; }
    public string? Revision { get; init; }
    public IReadOnlyList<HiddenPreviewItem>? HiddenItems { get; init; }
    public IReadOnlyList<FailedCombinedValidationPreviewItem>? FailedValidationRules { get; init; }
    public ScoringPreview? Scoring { get; init; }
}
public sealed record HiddenPreviewItem(string Name, string Kind, string Message, string ItemUrl, string? ConfigurationUrl);
public sealed record FailedCombinedValidationPreviewItem(string Name, string Message, string TargetName, string ItemUrl,
    string ConfigurationUrl, bool ShowWhileAnswering, bool AnyCheck, int SkippedCheckCount,
    IReadOnlyList<FailedCombinedCheckPreview> Checks);
public sealed record FailedCombinedCheckPreview(string Requirement, string LeftDescription, decimal? LeftValue,
    IReadOnlyList<string> LeftEffects, string RightDescription, decimal? RightValue, IReadOnlyList<string> RightEffects,
    bool Matches)
{
    public string? LeftValueText { get; init; }
    public string? RightValueText { get; init; }
}
public sealed record ScoringPreview(string? UnavailableReason, IReadOnlyList<string> CombinedDefinitionErrors,
    IReadOnlyList<ScoringCategoryPreview> Categories, IReadOnlyList<ScoringQuestionPreview> Questions,
    IReadOnlyList<CombinedScoringRulePreview>? CombinedRules);
public sealed record ScoringCategoryPreview(string Name, int Points);
public sealed record ScoringQuestionPreview(string Name, string CategoryName, string AnswerType, int Points, bool Applicable, bool Answered,
    string? AnswerNumberText, IReadOnlyList<string> SelectedOptions, string ScoringMode,
    IReadOnlyList<QuestionScoringRulePreview> Rules, string EditUrl);
public sealed record QuestionScoringRulePreview(string Name, string Requirement, string Outcome, bool Matches,
    bool Contributes, int Points, string? MatchedPointsText, string? UnitsText, int? Rate, int? Cap,
    bool CapReached, string? SupersedingRuleName, string EditUrl);
public sealed record CombinedScoringRulePreview(string Name, string CategoryName, string TargetName, string Outcome,
    string OutcomeCode, int AwardedPoints, string BandName, string? SupersedingRuleName, bool AnyCheck, int SkippedConditionCount,
    int SkippedCheckCount, IReadOnlyList<FailedCombinedCheckPreview> Conditions,
    IReadOnlyList<FailedCombinedCheckPreview> Checks, string ConfigurationUrl, string? TargetUrl);
