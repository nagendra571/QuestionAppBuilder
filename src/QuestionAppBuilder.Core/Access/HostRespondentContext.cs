using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Core.Options;

namespace QuestionAppBuilder.Core.Access;

public sealed class HostRespondentContext : IRespondentContext
{
    private readonly IHttpContextAccessor _accessor;
    private readonly QuestionAppBuilderRuntimeOptions _options;
    public HostRespondentContext(IHttpContextAccessor accessor, IOptions<QuestionAppBuilderRuntimeOptions> options)
    { _accessor = accessor; _options = options.Value; }

    public RespondentIdentity GetRequiredIdentity()
    {
        var context = _accessor.HttpContext ?? throw new InvalidOperationException("A respondent identity is required.");
        if (!string.IsNullOrWhiteSpace(_options.HostAuthenticationScheme))
        {
            var host = context.AuthenticateAsync(_options.HostAuthenticationScheme).GetAwaiter().GetResult();
            if (host.Succeeded && host.Principal?.Identity?.IsAuthenticated == true)
            {
                var issuer = host.Principal.FindFirst("iss")?.Value
                    ?? host.Principal.Claims.FirstOrDefault(c => c.Type == "sub")?.Issuer;
                var subject = host.Principal.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer)
                    || !StringComparer.Ordinal.Equals(issuer, _options.TrustedIssuer))
                    throw new InvalidOperationException("The authenticated host identity has an untrusted issuer or missing subject.");
                return new RespondentIdentity($"host:{issuer}:{subject}", null, null);
            }
        }
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst(ResponseGrantHandler.AccessTokenIdClaim) is null)
            throw new InvalidOperationException("The authenticated host identity could not be validated by the configured sign-in scheme.");
        var grant = context.AuthenticateAsync(ResponseGrantHandler.Scheme).GetAwaiter().GetResult();
        if (!grant.Succeeded || grant.Principal is null) throw new InvalidOperationException("A respondent identity is required.");
        var principal = grant.Principal;
        var owner = principal.FindFirstValue(ResponseGrantHandler.OwnerKeyClaim);
        var appText = principal.FindFirstValue(ResponseGrantHandler.ApplicationIdClaim);
        var versionText = principal.FindFirstValue(ResponseGrantHandler.VersionIdClaim);
        var tokenText = principal.FindFirstValue(ResponseGrantHandler.AccessTokenIdClaim);
        var responseText = principal.FindFirstValue(ResponseGrantHandler.ResponseIdClaim);
        if (string.IsNullOrWhiteSpace(owner) || !int.TryParse(appText, out _) || !int.TryParse(versionText, out var version)
            || !Guid.TryParse(tokenText, out var tokenId)) throw new InvalidOperationException("The response access grant is invalid.");
        Guid? responseId = Guid.TryParse(responseText, out var parsedResponseId) ? parsedResponseId : null;
        return new RespondentIdentity(owner, responseId, version, tokenId);
    }
}
