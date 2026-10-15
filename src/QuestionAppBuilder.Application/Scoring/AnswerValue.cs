namespace QuestionAppBuilder.Application.Scoring;

public sealed record AnswerValue(string? Text, decimal? Number, IReadOnlyCollection<int>? SelectedOptionIds);
