using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Editor.DependencyInjection;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.Routing;
using QuestionAppBuilder.Infrastructure.DependencyInjection;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.SampleHost.DevAuth;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["QuestionAppBuilder:ConnectionString"] ?? string.Empty;

builder.Services.AddQuestionAppBuilder(o => o.ConnectionString = connectionString);
builder.Services.AddQuestionAppBuilderEditor(o =>
{
    o.BasePath = builder.Configuration["QuestionAppBuilderEditor:BasePath"] ?? o.BasePath;
    o.DesignerRole = builder.Configuration["QuestionAppBuilderEditor:DesignerRole"] ?? o.DesignerRole;
});
builder.Services.AddSingleton<DevUserStore>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuestionAppBuilderDbContext>();
    if (db.Database.IsSqlServer())
    {
        db.Database.Migrate();
    }
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var editorOptions = app.Services.GetRequiredService<QuestionAppBuilderEditorOptions>();

app.MapQuestionAppBuilderEditor();
app.MapGet("/", () => Results.Redirect($"{editorOptions.BasePath.TrimEnd('/')}/Applications"));
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();

public partial class Program { }
