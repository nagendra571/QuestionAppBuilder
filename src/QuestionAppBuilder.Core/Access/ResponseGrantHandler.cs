using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using QuestionAppBuilder.Application.Access;

namespace QuestionAppBuilder.Core.Access;

public sealed class ResponseGrantHandler
{
    public const string Scheme = "QuestionAppBuilder.ResponseGrant";
    public const string OwnerKeyClaim = "qab:owner";
    public const string ApplicationIdClaim = "qab:application";
    public const string VersionIdClaim = "qab:version";
    public const string AccessTokenIdClaim = "qab:access-token";
    public const string ResponseIdClaim = "qab:response";

    public async Task SignInAsync(HttpContext context, AccessGrant grant, CancellationToken cancellationToken = default)
    {
        var claims = new List<Claim>
        {
            new(OwnerKeyClaim, grant.OwnerKey), new(ApplicationIdClaim, grant.ApplicationId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(VersionIdClaim, grant.ApplicationVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(AccessTokenIdClaim, grant.AccessTokenId.ToString("D"))
        };
        if (grant.ResponseId is Guid responseId) claims.Add(new Claim(ResponseIdClaim, responseId.ToString("D")));
        var identity = new ClaimsIdentity(claims, Scheme);
        await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        { IsPersistent = true, AllowRefresh = false, ExpiresUtc = new DateTimeOffset(DateTime.SpecifyKind(grant.ExpiresUtc, DateTimeKind.Utc)) });
    }
}
