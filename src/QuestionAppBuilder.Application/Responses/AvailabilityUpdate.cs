namespace QuestionAppBuilder.Application.Responses;

public sealed record AvailabilityUpdate(int ApplicationId, byte[] ExpectedRevision, bool AcceptNewResponses, bool AcceptSubmissions);
