using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Core.Access;
using QuestionAppBuilder.Core.DependencyInjection;
using QuestionAppBuilder.Core.Routing;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Editor.DependencyInjection;
using QuestionAppBuilder.Editor.Routing;
using QuestionAppBuilder.Infrastructure.DependencyInjection;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Reports.DependencyInjection;
using QuestionAppBuilder.Reports.Routing;

const string packageCookie = "PackageHost.Cookie";
const string packageIssuer = "https://package-consumer.example.test";
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["QuestionAppBuilder:ConnectionString"]
    ?? Environment.GetEnvironmentVariable("QAB_PACKAGE_SQL");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("The package consumer requires QAB_PACKAGE_SQL with a disposable SQL Server catalog.");
var publicBaseUri = Environment.GetEnvironmentVariable("QAB_PACKAGE_BASE_URL")
    ?? "https://localhost";

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PackageResponseEmailSender>();
builder.Services.AddSingleton<IResponseEmailSender>(provider => provider.GetRequiredService<PackageResponseEmailSender>());
builder.Services.AddScoped<ITrustedActorContext, PackageTrustedActorContext>();
builder.Services.AddQuestionAppBuilder(options => options.ConnectionString = connectionString);
builder.Services.AddQuestionAppBuilderEditor(options =>
{
    options.BasePath = "/Builder";
    options.DesignPolicy = "PackageDesign";
    options.LayoutName = "_Layout";
});
builder.Services.AddQuestionAppBuilderRuntime(options =>
{
    options.BasePath = "/Forms";
    options.FillPolicy = "PackageFill";
    options.HostAuthenticationScheme = packageCookie;
    options.TrustedIssuer = packageIssuer;
    options.LayoutName = "_Layout";
    options.PublicBaseUri = new Uri(publicBaseUri, UriKind.Absolute);
});
builder.Services.AddQuestionAppBuilderReports(options =>
{
    options.BasePath = "/Insights";
    options.ReportPolicy = "PackageReport";
    options.LayoutName = "_Layout";
});
builder.Services.AddAuthentication(packageCookie)
    .AddCookie(packageCookie, options =>
    {
        options.Cookie.Name = "PackageHost.Cookie";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PackageDesign", policy => policy.RequireAuthenticatedUser().RequireRole("Designer"));
    options.AddPolicy("PackageFill", policy => policy.RequireAssertion(async context =>
    {
        if (context.User.IsInRole("Filler")) return true;
        if (context.Resource is not HttpContext http) return false;
        var grant = await http.AuthenticateAsync(ResponseGrantHandler.Scheme);
        return grant.Succeeded && grant.Principal?.Identity?.IsAuthenticated == true;
    }));
    options.AddPolicy("PackageReport", policy => policy.RequireAuthenticatedUser().RequireRole("Analyst"));
});

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<QuestionAppBuilderDbContext>();
    db.Database.Migrate();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/PackageTest/SignIn/{role}", async (HttpContext context, string role, string? next) =>
{
    if (role is not ("Designer" or "Filler" or "Analyst")) return Results.BadRequest("Unknown test role.");
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, "package-test-" + role.ToLowerInvariant()),
        new Claim(ClaimTypes.Role, role),
        new Claim("iss", packageIssuer),
        new Claim("sub", "package-test-" + role.ToLowerInvariant())
    }, packageCookie);
    await context.SignInAsync(packageCookie, new ClaimsPrincipal(identity));
    return Results.Redirect(string.IsNullOrWhiteSpace(next) || !next.StartsWith('/') || next.StartsWith("//", StringComparison.Ordinal)
        ? "/" : next);
}).AllowAnonymous();
app.MapGet("/PackageTest/LastEmail", (PackageResponseEmailSender sender) =>
    sender.LastLink is Uri link ? Results.Text(link.ToString()) : Results.NotFound()).AllowAnonymous();
app.MapPost("/PackageTest/Shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Ok();
}).AllowAnonymous();
app.MapQuestionAppBuilderEditor();
app.MapQuestionAppBuilderRuntime();
app.MapQuestionAppBuilderReports();
app.MapControllerRoute("package-host-area", "{area:exists}/{controller=Applications}/{action=Index}/{id?}");
app.Run();

public sealed class PackageResponseEmailSender : IResponseEmailSender
{
    private readonly object _gate = new();
    public Uri? LastLink { get; private set; }
    public string? LastRecipient { get; private set; }
    public Task SendLinkAsync(string email, Uri link, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { LastRecipient = email; LastLink = link; }
        return Task.CompletedTask;
    }
}

public sealed class PackageTrustedActorContext : ITrustedActorContext
{
    private readonly IHttpContextAccessor _accessor;
    public PackageTrustedActorContext(IHttpContextAccessor accessor) => _accessor = accessor;
    public string GetRequiredActorKey()
    {
        var user = _accessor.HttpContext?.User;
        var issuer = user?.FindFirst("iss")?.Value;
        var subject = user?.FindFirst("sub")?.Value;
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException("A trusted package-host actor is required.");
        return $"{issuer}:{subject}";
    }
}
