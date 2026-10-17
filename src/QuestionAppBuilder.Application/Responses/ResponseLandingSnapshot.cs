using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Responses;

public sealed record ResponseLandingSnapshot(int ApplicationId, string ApplicationName, ApplicationVersionGraph Definition,
    bool AcceptingNewResponses, bool AcceptingSubmissions);
