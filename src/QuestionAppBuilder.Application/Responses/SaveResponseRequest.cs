using QuestionAppBuilder.Application.Scoring;

namespace QuestionAppBuilder.Application.Responses;

public sealed record SaveResponseRequest(Guid ResponseId, byte[] ExpectedRevision, IReadOnlyDictionary<int, AnswerValue> Answers);
