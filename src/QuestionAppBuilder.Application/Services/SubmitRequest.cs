using QuestionAppBuilder.Application.Scoring;

namespace QuestionAppBuilder.Application.Services;

public sealed record SubmitRequest(
    int ApplicationId,
    int ApplicationVersionId,
    IReadOnlyDictionary<int, AnswerValue> Answers,
    string? UserId = null);
