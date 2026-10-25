using System.Text.Json.Serialization;
using QuestionAppBuilder.Application.Composition;

namespace QuestionAppBuilder.Application.Scoring;

public sealed record QuestionScore(int QuestionId, int Points)
{
    public bool IsApplicable { get; init; } = true;
    public IReadOnlyList<RuleEvaluation> Rules { get; init; } = Array.Empty<RuleEvaluation>();
}

public sealed record RuleEvaluation(int RuleId, bool Matches, bool Contributes, int Points);

public sealed record CategoryScore(int CategoryId, int Score);

public sealed record SubmissionScore(int Total, IReadOnlyList<QuestionScore> Questions, IReadOnlyList<CategoryScore> Categories, string EngineVersion = "1.0")
{
    [JsonIgnore]
    public ScoringDiagnostics? Diagnostics { get; init; }
}

public sealed record ScoringDiagnostics(IReadOnlyList<QuestionScoringDiagnostic> Questions,
    IReadOnlyList<CombinedScoreRuleDiagnostic>? CombinedRules, string? UnavailableReason = null,
    IReadOnlyList<string>? CombinedDefinitionErrors = null);

public sealed record QuestionScoringDiagnostic(int QuestionId, bool IsApplicable, bool Answered,
    decimal? AnswerNumber, IReadOnlyList<int> SelectedOptionIds, IReadOnlyList<QuestionScoringRuleDiagnostic> Rules);

public sealed record QuestionScoringRuleDiagnostic(int RuleId, string Outcome, int? MatchedPoints,
    decimal? Units, int? Rate, int? Cap, bool CapReached, bool Matches, bool Contributes, int Points,
    int? SupersedingRuleId);
