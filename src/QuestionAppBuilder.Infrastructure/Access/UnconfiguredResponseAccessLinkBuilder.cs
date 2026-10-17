using QuestionAppBuilder.Application.Access;

namespace QuestionAppBuilder.Infrastructure.Access;

public sealed class UnconfiguredResponseAccessLinkBuilder : IUnconfiguredResponseAccessLinkBuilder
{
    public Uri CreateLink(string token) => throw new InvalidOperationException("A trusted response access link builder must be configured before email links are sent.");
}
