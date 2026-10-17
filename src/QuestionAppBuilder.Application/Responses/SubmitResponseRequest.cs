namespace QuestionAppBuilder.Application.Responses;

public sealed record SubmitResponseRequest(Guid ResponseId, byte[] ExpectedRevision, Guid RequestId);
