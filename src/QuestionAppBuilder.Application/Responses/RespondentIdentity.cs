namespace QuestionAppBuilder.Application.Responses;

public sealed record RespondentIdentity(string OwnerKey, Guid? GrantedResponseId, int? GrantedVersionId);
