namespace QuestionAppBuilder.Application.Responses;

public sealed record ResponseSummary(Guid Id, int ApplicationId, int ApplicationVersionId, string DisplayName, DateTime UpdatedUtc);
