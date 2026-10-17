using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Core.Options;

namespace QuestionAppBuilder.SampleHost.Integration;

public sealed class HostTrustedActorContext : ITrustedActorContext
{
    private readonly IHttpContextAccessor _accessor;
    private readonly QuestionAppBuilderRuntimeOptions _options;

    public HostTrustedActorContext(IHttpContextAccessor accessor, IOptions<QuestionAppBuilderRuntimeOptions> options)
    {
        _accessor = accessor;
        _options = options.Value;
    }

    public string GetRequiredActorKey()
    {
        var context = _accessor.HttpContext ?? throw new InvalidOperationException("A trusted administrative actor is required.");
        if (string.IsNullOrWhiteSpace(_options.HostAuthenticationScheme) || string.IsNullOrWhiteSpace(_options.TrustedIssuer))
            throw new InvalidOperationException("The host actor identity is not configured.");
        var result = context.AuthenticateAsync(_options.HostAuthenticationScheme).GetAwaiter().GetResult();
        var principal = result.Succeeded ? result.Principal : null;
        var issuer = principal?.FindFirst("iss")?.Value ?? principal?.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Issuer;
        var subject = principal?.FindFirst("sub")?.Value;
        if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)
            || !StringComparer.Ordinal.Equals(issuer, _options.TrustedIssuer))
            throw new InvalidOperationException("A trusted administrative actor identity is required.");
        return $"host:{issuer}:{subject}";
    }
}
