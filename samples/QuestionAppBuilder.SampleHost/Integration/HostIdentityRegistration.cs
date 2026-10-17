using System.Security.Claims;
using MailKit.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Core.Access;
using QuestionAppBuilder.SampleHost.DevAuth;

namespace QuestionAppBuilder.SampleHost.Integration;

public sealed record HostIdentitySettings(string Issuer, string AuthenticationScheme, bool Development);

public static class HostIdentityRegistration
{
    public const string DesignPolicy = "QuestionAppBuilder.Design";
    public const string FillPolicy = "QuestionAppBuilder.Fill";
    public const string ReportPolicy = "QuestionAppBuilder.Report";
    public const string CookieScheme = "QuestionAppBuilder.HostCookie";
    public const string OidcScheme = "QuestionAppBuilder.Oidc";
    public const string DevelopmentIssuer = "https://dev.questionappbuilder.local";

    public static HostIdentitySettings AddHostIdentity(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddHttpContextAccessor();
        if (environment.IsDevelopment())
        {
            services.AddSingleton<DevUserStore>();
            services.AddSingleton<DevMailSink>();
            services.AddSingleton<IResponseEmailSender, DevelopmentResponseEmailSender>();
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieScheme;
                options.DefaultSignInScheme = CookieScheme;
                options.DefaultChallengeScheme = CookieScheme;
            }).AddCookie(CookieScheme, ConfigureCookie);
            return new HostIdentitySettings(DevelopmentIssuer, CookieScheme, Development: true);
        }

        var authority = configuration["QuestionAppBuilder:Identity:Authority"];
        var issuer = configuration["QuestionAppBuilder:Identity:Issuer"];
        var clientId = configuration["QuestionAppBuilder:Identity:ClientId"];
        var clientSecret = configuration["QuestionAppBuilder:Identity:ClientSecret"];
        var keyRingPath = configuration["QuestionAppBuilder:DataProtection:KeyRingPath"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(keyRingPath) || !Path.IsPathFullyQualified(keyRingPath))
            throw new InvalidOperationException("Production requires HTTPS OIDC Authority and Issuer, ClientId, ClientSecret, and an absolute DataProtection:KeyRingPath.");

        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CookieScheme;
            options.DefaultSignInScheme = CookieScheme;
            options.DefaultChallengeScheme = OidcScheme;
        })
            .AddCookie(CookieScheme, ConfigureCookie)
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = authorityUri.ToString().TrimEnd('/');
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuerUri.ToString().TrimEnd('/'),
                    NameClaimType = "name",
                    RoleClaimType = configuration["QuestionAppBuilder:Identity:RoleClaimType"] ?? "roles"
                };
                options.Events.OnTokenValidated = context =>
                {
                    var principalIssuer = context.Principal?.FindFirst("iss")?.Value
                        ?? context.Principal?.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Issuer;
                    var subject = context.Principal?.FindFirst("sub")?.Value;
                    if (!StringComparer.Ordinal.Equals(principalIssuer, issuerUri.ToString().TrimEnd('/')) || string.IsNullOrWhiteSpace(subject))
                        context.Fail("The identity provider did not issue the configured issuer and a stable subject.");
                    return Task.CompletedTask;
                };
            });

        var smtp = configuration.GetSection("QuestionAppBuilder:Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.FromAddress) || smtp.Port is < 1 or > 65535 ||
            (string.IsNullOrWhiteSpace(smtp.Username) != string.IsNullOrWhiteSpace(smtp.Password)) ||
            !MimeKit.MailboxAddress.TryParse(smtp.FromAddress, out _))
            throw new InvalidOperationException("Production requires SMTP Host, Port, FromAddress, and either both or neither Username and Password.");
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection("QuestionAppBuilder:Smtp")).ValidateOnStart();
        services.AddSingleton<IResponseEmailSender, SmtpResponseEmailSender>();
        return new HostIdentitySettings(issuerUri.ToString().TrimEnd('/'), CookieScheme, Development: false);
    }

    public static IServiceCollection AddPlatformPolicies(this IServiceCollection services, IConfiguration configuration)
    {
        var designRole = configuration["QuestionAppBuilder:Authorization:DesignRole"] ?? "Designer";
        var reportRole = configuration["QuestionAppBuilder:Authorization:ReportRole"] ?? "Analyst";
        if (string.IsNullOrWhiteSpace(designRole) || string.IsNullOrWhiteSpace(reportRole))
            throw new InvalidOperationException("Design and Report authorization roles must be configured.");

        services.AddAuthorization(options =>
        {
            options.AddPolicy(DesignPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(designRole));
            options.AddPolicy(FillPolicy, policy => policy.RequireAssertion(async context =>
            {
                if (context.User.Identity?.IsAuthenticated == true) return true;
                if (context.Resource is not HttpContext http) return false;
                var grant = await http.AuthenticateAsync(ResponseGrantHandler.Scheme);
                return grant.Succeeded && grant.Principal?.Identity?.IsAuthenticated == true;
            }));
            options.AddPolicy(ReportPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(reportRole));
        });
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "__Host-QAB.Host";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
