namespace QuestionAppBuilder.Application.Scoring;

public sealed record QuestionScore(int QuestionId, int Points)
{
    public bool IsApplicable { get; init; } = true;
    public IReadOnlyList<RuleEvaluation> Rules { get; init; } = Array.Empty<RuleEvaluation>();
}

public sealed record RuleEvaluation(int RuleId, bool Matches, bool Contributes, int Points);

public sealed record CategoryScore(int CategoryId, int Score);

public sealed record SubmissionScore(int Total, IReadOnlyList<QuestionScore> Questions, IReadOnlyList<CategoryScore> Categories, string EngineVersion = "1.0");
