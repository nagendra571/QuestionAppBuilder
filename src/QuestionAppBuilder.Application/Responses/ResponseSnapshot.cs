using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Responses;

public sealed record ResponseSnapshot(Guid Id, int ApplicationId, int ApplicationVersionId, ApplicationVersionGraph Definition,
    IReadOnlyDictionary<int, AnswerValue> Answers, ResponseStatus Status, byte[] Revision, DateTime UpdatedUtc,
    bool AcceptingNewResponses, bool AcceptingSubmissions);
