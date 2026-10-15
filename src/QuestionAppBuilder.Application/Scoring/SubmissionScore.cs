namespace QuestionAppBuilder.Application.Scoring;

public sealed record QuestionScore(int QuestionId, int Points);

public sealed record CategoryScore(int CategoryId, int Score);

public sealed record SubmissionScore(int Total, IReadOnlyList<QuestionScore> Questions, IReadOnlyList<CategoryScore> Categories);
