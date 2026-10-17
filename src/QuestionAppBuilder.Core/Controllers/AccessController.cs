using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Core.Access;
using QuestionAppBuilder.Core.Options;
using QuestionAppBuilder.Domain.Exceptions;

namespace QuestionAppBuilder.Core.Controllers;

[AllowAnonymous]
public sealed class AccessController : Controller
{
    private readonly IResponseAccessService _access;
    private readonly ResponseGrantHandler _grantHandler;
    private readonly QuestionAppBuilderRuntimeOptions _options;
    private readonly IAntiforgery _antiforgery;
    public AccessController(IResponseAccessService access, ResponseGrantHandler grantHandler, IOptions<QuestionAppBuilderRuntimeOptions> options, IAntiforgery antiforgery)
    { _access = access; _grantHandler = grantHandler; _options = options.Value; _antiforgery = antiforgery; }

    [ActionName("Request")]
    [HttpPost]
    [EnableRateLimiting("qab-email-access-ip")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestLink([FromForm] int applicationId, [FromForm] Guid? responseId, [FromForm] string email, CancellationToken cancellationToken)
    {
        await _access.RequestAsync(new AccessLinkRequest(applicationId, responseId, email), cancellationToken);
        return Content("If the address matches a response, an access link has been sent.", "text/plain");
    }

    [HttpGet]
    public IActionResult Confirm([FromQuery] string token)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store";
        var antiforgery = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        var encodedToken = HtmlEncoder.Default.Encode(token ?? string.Empty);
        var encodedAntiforgery = HtmlEncoder.Default.Encode(antiforgery ?? string.Empty);
        return Content($"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Confirm access</title><body><main><h1>Continue to your response</h1><p>Confirm to open your saved response.</p><form method=\"post\" action=\"{_options.BasePath.TrimEnd('/')}/Access/Redeem\"><input type=\"hidden\" name=\"token\" value=\"{encodedToken}\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{encodedAntiforgery}\"><button type=\"submit\">Continue</button></form></main></body></html>", "text/html");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Redeem([FromForm] string token, CancellationToken cancellationToken)
    {
        try
        {
            var grant = await _access.RedeemAsync(token, cancellationToken);
            await _grantHandler.SignInAsync(HttpContext, grant, cancellationToken);
            return LocalRedirect($"{_options.BasePath.TrimEnd('/')}/{grant.ApplicationId}");
        }
        catch (InvalidAccessLinkException) { return BadRequest("This access link is invalid, expired, or already used."); }
    }
}
