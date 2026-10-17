using QuestionAppBuilder.Application.Authoring;

namespace QuestionAppBuilder.Infrastructure.Authoring;

public sealed class UnconfiguredTrustedActorContext : ITrustedActorContext
{
    public string GetRequiredActorKey() => throw new InvalidOperationException("A trusted administrative actor context must be configured before changing availability.");
}
