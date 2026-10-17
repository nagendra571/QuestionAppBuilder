namespace QuestionAppBuilder.Application.Authoring;

/// <summary>Provides a host-authenticated actor key for audited administrative changes.</summary>
public interface ITrustedActorContext
{
    string GetRequiredActorKey();
}
