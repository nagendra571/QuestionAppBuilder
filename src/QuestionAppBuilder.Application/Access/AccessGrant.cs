namespace QuestionAppBuilder.Application.Access;

public sealed record AccessGrant(Guid AccessTokenId, string OwnerKey, int ApplicationId, int ApplicationVersionId,
    Guid? ResponseId, DateTime ExpiresUtc);
