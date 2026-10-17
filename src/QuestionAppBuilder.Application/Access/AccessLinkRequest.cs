namespace QuestionAppBuilder.Application.Access;

public sealed record AccessLinkRequest(int ApplicationId, Guid? ResponseId, string Email);
