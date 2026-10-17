namespace QuestionAppBuilder.Application.Authoring;

public sealed record PublishRequest(int ApplicationId, byte[] ExpectedRevision, IReadOnlySet<string> AcknowledgedWarningCodes);
