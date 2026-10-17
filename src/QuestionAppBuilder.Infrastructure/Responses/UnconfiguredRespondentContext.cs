using QuestionAppBuilder.Application.Responses;

namespace QuestionAppBuilder.Infrastructure.Responses;

internal sealed class UnconfiguredRespondentContext : IRespondentContext
{
    public RespondentIdentity GetRequiredIdentity() => throw new InvalidOperationException(
        "The host must register an IRespondentContext implementation before response services can be used.");
}
