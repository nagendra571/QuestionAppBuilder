using QuestionAppBuilder.Application.Access;

namespace QuestionAppBuilder.Infrastructure.Access;

public sealed class UnconfiguredResponseEmailSender : IResponseEmailSender
{
    public Task SendLinkAsync(string email, Uri link, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("A host response email sender must be configured before email-link access is used.");
}
