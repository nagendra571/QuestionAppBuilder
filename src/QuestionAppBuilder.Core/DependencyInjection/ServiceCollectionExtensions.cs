using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Core.Access;
using QuestionAppBuilder.Core.Options;

namespace QuestionAppBuilder.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuestionAppBuilderRuntime(this IServiceCollection services,
        Action<QuestionAppBuilderRuntimeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new QuestionAppBuilderRuntimeOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.FillPolicy)) throw new InvalidOperationException("A host fill authorization policy is required.");
        if (!options.BasePath.StartsWith('/') || options.BasePath.Contains("..", StringComparison.Ordinal)) throw new InvalidOperationException("BasePath must be a rooted application path.");
        if (options.EmailLinkLifetime <= TimeSpan.Zero) throw new InvalidOperationException("EmailLinkLifetime must be positive.");
        if (options.AccessRequestsPerIp < 1 || options.AccessRequestIpWindow <= TimeSpan.Zero) throw new InvalidOperationException("The access request IP rate limit must be positive.");
        if (options.PublicBaseUri is not null && (!options.PublicBaseUri.IsAbsoluteUri || options.PublicBaseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(options.PublicBaseUri.UserInfo)))
            throw new InvalidOperationException("PublicBaseUri must be a trusted HTTPS origin.");
        services.Configure<QuestionAppBuilderRuntimeOptions>(o =>
        {
            o.FillPolicy = options.FillPolicy; o.HostAuthenticationScheme = options.HostAuthenticationScheme; o.TrustedIssuer = options.TrustedIssuer;
            o.BasePath = options.BasePath.TrimEnd('/'); o.LayoutName = options.LayoutName; o.EmailLinkLifetime = options.EmailLinkLifetime; o.PublicBaseUri = options.PublicBaseUri;
            o.AccessRequestsPerIp = options.AccessRequestsPerIp; o.AccessRequestIpWindow = options.AccessRequestIpWindow;
        });
        services.Configure<ResponseAccessOptions>(o => o.LinkLifetime = options.EmailLinkLifetime);
        services.AddHttpContextAccessor();
        services.AddControllersWithViews().AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly);
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "__Host-QAB.Antiforgery";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddRateLimiter(o => o.AddPolicy("qab-email-access-ip", context =>
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = options.AccessRequestsPerIp, Window = options.AccessRequestIpWindow, QueueLimit = 0, AutoReplenishment = true });
        }));
        var respondentDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IRespondentContext));
        if (respondentDescriptor?.ImplementationType is Type existingType && typeof(IUnconfiguredRespondentContext).IsAssignableFrom(existingType))
            services.Replace(ServiceDescriptor.Scoped<IRespondentContext, HostRespondentContext>());
        else services.TryAddScoped<IRespondentContext, HostRespondentContext>();
        var linkBuilderDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IResponseAccessLinkBuilder));
        if (linkBuilderDescriptor?.ImplementationType is Type linkBuilderType && typeof(IUnconfiguredResponseAccessLinkBuilder).IsAssignableFrom(linkBuilderType))
            services.Replace(ServiceDescriptor.Scoped<IResponseAccessLinkBuilder, ResponseAccessLinkBuilder>());
        else services.TryAddScoped<IResponseAccessLinkBuilder, ResponseAccessLinkBuilder>();
        services.TryAddScoped<ResponseGrantHandler>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddAuthentication().AddCookie(ResponseGrantHandler.Scheme, cookie =>
        {
            cookie.Cookie.Name = "__Host-QAB.ResponseGrant";
            cookie.Cookie.Path = "/";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.SlidingExpiration = false;
            cookie.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        });
        services.AddAuthorization(o => o.AddPolicy(options.FillPolicy, p => p.RequireAuthenticatedUser()));
        return services;
    }
}
