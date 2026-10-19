using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Core.DependencyInjection;
using QuestionAppBuilder.Core.Routing;
using QuestionAppBuilder.Core.Options;
using QuestionAppBuilder.Editor.DependencyInjection;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.Routing;
using QuestionAppBuilder.Infrastructure.DependencyInjection;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Reports.DependencyInjection;
using QuestionAppBuilder.Reports.Options;
using QuestionAppBuilder.Reports.Routing;
using QuestionAppBuilder.SampleHost.Integration;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["QuestionAppBuilder:ConnectionString"] ?? string.Empty;
var hostIdentity = HostIdentityRegistration.AddHostIdentity(builder.Services, builder.Configuration, builder.Environment);
builder.Services.AddScoped<ITrustedActorContext, HostTrustedActorContext>();

builder.Services.AddQuestionAppBuilder(o => o.ConnectionString = connectionString);
builder.Services.AddQuestionAppBuilderRuntime(o =>
{
    o.BasePath = builder.Configuration["QuestionAppBuilderRuntime:BasePath"] ?? "/App";
    o.FillPolicy = HostIdentityRegistration.FillPolicy;
    o.HostAuthenticationScheme = hostIdentity.AuthenticationScheme;
    o.TrustedIssuer = hostIdentity.Issuer;
    o.PublicBaseUri = Uri.TryCreate(builder.Configuration["QuestionAppBuilderRuntime:PublicBaseUri"], UriKind.Absolute, out var publicUri)
        ? publicUri
        : new Uri("https://localhost");
});
builder.Services.AddQuestionAppBuilderEditor(o =>
{
    o.BasePath = builder.Configuration["QuestionAppBuilderEditor:BasePath"] ?? o.BasePath;
    o.DesignPolicy = builder.Configuration["QuestionAppBuilderEditor:DesignPolicy"] ?? o.DesignPolicy;
});
HostIdentityRegistration.AddPlatformPolicies(builder.Services, builder.Configuration);
builder.Services.AddQuestionAppBuilderReports(o =>
{
    o.BasePath = builder.Configuration["QuestionAppBuilderReports:BasePath"] ?? o.BasePath;
    o.ReportPolicy = HostIdentityRegistration.ReportPolicy;
    o.LayoutName = builder.Configuration["QuestionAppBuilderReports:LayoutName"] ?? o.LayoutName;
    o.DisplayTimeZone = builder.Configuration["QuestionAppBuilderReports:DisplayTimeZone"] ?? o.DisplayTimeZone;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuestionAppBuilderDbContext>();
    if (builder.Environment.IsDevelopment() && db.Database.IsSqlServer())
    {
        db.Database.Migrate();
    }
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapQuestionAppBuilderEditor();
app.MapQuestionAppBuilderRuntime();
app.MapQuestionAppBuilderReports();
app.MapControllerRoute(name: "area-conventional", pattern: "{area:exists}/{controller=Applications}/{action=Index}/{id?}");
app.MapGet("/", async (HttpContext context, IAuthorizationService authorization,
    [FromServices] QuestionAppBuilderEditorOptions editorOptions, IOptions<QuestionAppBuilderRuntimeOptions> runtimeOptions,
    [FromServices] QuestionAppBuilderReportsOptions reportsOptions) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Challenge();

    if ((await authorization.AuthorizeAsync(context.User, context, HostIdentityRegistration.DesignPolicy)).Succeeded)
        return Results.Redirect(SurfacePath(context.Request.PathBase, editorOptions.BasePath, "Applications"));
    if ((await authorization.AuthorizeAsync(context.User, context, HostIdentityRegistration.FillPolicy)).Succeeded)
        return Results.Redirect(SurfacePath(context.Request.PathBase, runtimeOptions.Value.BasePath, "Mine"));
    if ((await authorization.AuthorizeAsync(context.User, context, HostIdentityRegistration.ReportPolicy)).Succeeded)
        return Results.Redirect(SurfacePath(context.Request.PathBase, reportsOptions.BasePath, string.Empty, trailingSlash: true));

    return Results.Forbid();
});
if (builder.Environment.IsDevelopment())
{
    app.MapControllerRoute(name: "development-account", pattern: "{controller=Account}/{action=Login}/{id?}");
}

app.Run();

static string SurfacePath(PathString pathBase, string basePath, string endpoint, bool trailingSlash = false)
{
    var relativePath = string.Join('/', new[] { basePath, endpoint }.Select(segment => segment.Trim('/')).Where(segment => segment.Length > 0));
    var targetPath = relativePath.Length == 0 ? new PathString("/") : new PathString("/" + relativePath);
    var result = pathBase.Add(targetPath).Value ?? "/";
    return trailingSlash && !result.EndsWith('/') ? result + "/" : result;
}

public partial class Program { }
