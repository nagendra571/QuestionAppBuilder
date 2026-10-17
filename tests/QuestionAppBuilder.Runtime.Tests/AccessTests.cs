using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Core.Access;
using QuestionAppBuilder.Core.DependencyInjection;
using QuestionAppBuilder.Core.Options;
using QuestionAppBuilder.Core.Routing;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class AccessTests
{
    [Fact]
    public void Unsafe_redirect_is_rejected()
    {
        var builder = new ResponseAccessLinkBuilder(Options.Create(new QuestionAppBuilderRuntimeOptions { PublicBaseUri = new Uri("http://untrusted.example.test") }));
        Action create = () => builder.CreateLink("opaque-token");
        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Access_link_uses_configured_origin_and_custom_base_path()
    {
        var builder = new ResponseAccessLinkBuilder(Options.Create(new QuestionAppBuilderRuntimeOptions
        { PublicBaseUri = new Uri("https://forms.example.test"), BasePath = "/tenant/questionnaires" }));
        var link = builder.CreateLink("opaque-token");
        link.GetLeftPart(UriPartial.Authority).Should().Be("https://forms.example.test");
        link.AbsolutePath.Should().Be("/tenant/questionnaires/Access/Confirm");
        link.Query.Should().Contain("opaque-token");
    }

    [Fact]
    public void Signed_in_wrong_issuer_cannot_access_response()
    {
        var context = HttpContextWithAuthentication(scheme => scheme == "HostOidc"
            ? Success("HostOidc", new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("iss", "https://wrong.test"), new Claim("sub", "user-1") }, "HostOidc")))
            : AuthenticateResult.NoResult());
        var accessor = new HttpContextAccessor { HttpContext = context };
        var respondent = new HostRespondentContext(accessor, Options.Create(new QuestionAppBuilderRuntimeOptions
        { HostAuthenticationScheme = "HostOidc", TrustedIssuer = "https://trusted.test" }));
        Action get = () => respondent.GetRequiredIdentity();
        get.Should().Throw<InvalidOperationException>().WithMessage("*untrusted issuer*");
    }

    [Fact]
    public void Signed_in_host_identity_wins_over_stale_email_cookie()
    {
        var context = HttpContextWithAuthentication(scheme => scheme switch
        {
            "HostOidc" => Success(scheme, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("iss", "https://trusted.test"), new Claim("sub", "signed-in-user") }, scheme))),
            ResponseGrantHandler.Scheme => Success(scheme, new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ResponseGrantHandler.OwnerKeyClaim, "email:stale"), new Claim(ResponseGrantHandler.ApplicationIdClaim, "3"),
                new Claim(ResponseGrantHandler.VersionIdClaim, "2"), new Claim(ResponseGrantHandler.AccessTokenIdClaim, Guid.NewGuid().ToString())
            }, scheme))),
            _ => AuthenticateResult.NoResult()
        });
        var identity = new HostRespondentContext(new HttpContextAccessor { HttpContext = context }, Options.Create(new QuestionAppBuilderRuntimeOptions
        { HostAuthenticationScheme = "HostOidc", TrustedIssuer = "https://trusted.test" })).GetRequiredIdentity();
        identity.OwnerKey.Should().Be("host:https://trusted.test:signed-in-user");
    }

    [Fact]
    public void Unrecognized_signed_in_user_cannot_fall_back_to_email_grant()
    {
        var hostPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "other-signed-in-user") }, "OtherHostScheme"));
        var context = HttpContextWithAuthentication(scheme => scheme == ResponseGrantHandler.Scheme
            ? Success(scheme, new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ResponseGrantHandler.OwnerKeyClaim, "email:stale"), new Claim(ResponseGrantHandler.ApplicationIdClaim, "3"),
                new Claim(ResponseGrantHandler.VersionIdClaim, "2"), new Claim(ResponseGrantHandler.AccessTokenIdClaim, Guid.NewGuid().ToString())
            }, scheme))) : AuthenticateResult.NoResult());
        context.User = hostPrincipal;
        var respondent = new HostRespondentContext(new HttpContextAccessor { HttpContext = context }, Options.Create(new QuestionAppBuilderRuntimeOptions()));
        Action get = () => respondent.GetRequiredIdentity();
        get.Should().Throw<InvalidOperationException>().WithMessage("*could not be validated*");
    }

    [Fact]
    public void Missing_fill_policy_fails_closed()
    {
        var services = new ServiceCollection();
        Action add = () => services.AddQuestionAppBuilderRuntime(_ => { });
        add.Should().Throw<InvalidOperationException>().WithMessage("*fill authorization policy*");
    }

    [Fact]
    public void Email_link_get_displays_confirmation_without_consuming_token()
    {
        var access = new FakeAccessService();
        var controller = new QuestionAppBuilder.Core.Controllers.AccessController(access, new ResponseGrantHandler(),
            Options.Create(new QuestionAppBuilderRuntimeOptions { BasePath = "/custom/App" }), new FakeAntiforgery())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = controller.Confirm("opaque-token") as ContentResult;
        result!.Content.Should().Contain("Continue");
        controller.Response.Headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
        access.RedeemCount.Should().Be(0);
    }

    [Fact]
    public async Task Redeemed_response_grant_redirects_directly_to_saved_response()
    {
        var responseId = Guid.NewGuid();
        var access = new FakeAccessService(new QuestionAppBuilder.Application.Access.AccessGrant(Guid.NewGuid(), "email:owner", 3, 7,
            responseId, DateTime.UtcNow.AddMinutes(10)));
        var context = HttpContextWithAuthentication(_ => AuthenticateResult.NoResult());
        var controller = new QuestionAppBuilder.Core.Controllers.AccessController(access, new ResponseGrantHandler(),
            Options.Create(new QuestionAppBuilderRuntimeOptions { BasePath = "/custom/App" }), new FakeAntiforgery())
        { ControllerContext = new ControllerContext { HttpContext = context } };

        var result = await controller.Redeem("opaque-token", CancellationToken.None) as LocalRedirectResult;

        result!.Url.Should().Be($"/custom/App/Responses/{responseId}");
    }

    [Fact]
    public async Task Custom_base_path_confirmation_route_keeps_email_scanner_from_consuming_token()
    {
        var access = new FakeAccessService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuestionAppBuilderRuntime(o => { o.FillPolicy = "Fill"; o.BasePath = "/tenant/App"; o.PublicBaseUri = new Uri("https://forms.example.test"); });
        builder.Services.AddSingleton<QuestionAppBuilder.Application.Access.IResponseAccessService>(access);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapQuestionAppBuilderRuntime();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.GetAsync("https://localhost/tenant/App/Access/Confirm?token=opaque-token");
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        html.Should().Contain("name=\"token\"");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        access.RedeemCount.Should().Be(0);
    }

    private static DefaultHttpContext HttpContextWithAuthentication(Func<string, AuthenticateResult> authenticate)
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeAuthenticationService(authenticate));
        context.RequestServices = services.BuildServiceProvider();
        return context;
    }
    private static AuthenticateResult Success(string scheme, ClaimsPrincipal principal) => AuthenticateResult.Success(new AuthenticationTicket(principal, scheme));

    private sealed class FakeAuthenticationService(Func<string, AuthenticateResult> authenticate) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(authenticate(scheme ?? string.Empty));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
    private sealed class FakeAccessService(QuestionAppBuilder.Application.Access.AccessGrant? grant = null) : QuestionAppBuilder.Application.Access.IResponseAccessService
    {
        public int RedeemCount { get; private set; }
        public Task RequestAsync(QuestionAppBuilder.Application.Access.AccessLinkRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<QuestionAppBuilder.Application.Access.AccessGrant> RedeemAsync(string token, CancellationToken cancellationToken = default)
        { RedeemCount++; return grant is null ? Task.FromException<QuestionAppBuilder.Application.Access.AccessGrant>(new InvalidOperationException()) : Task.FromResult(grant); }
    }
    private sealed class FakeAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) => new("request-token", "cookie-token", "__RequestVerificationToken", "X-CSRF");
        public AntiforgeryTokenSet GetTokens(HttpContext context) => GetAndStoreTokens(context);
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);
        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
    }
}
