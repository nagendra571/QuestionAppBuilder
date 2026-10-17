using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Core.DependencyInjection;
using QuestionAppBuilder.Core.Routing;
using QuestionAppBuilder.Editor.DependencyInjection;
using QuestionAppBuilder.Editor.Routing;
using QuestionAppBuilder.Editor.ViewModels;
using QuestionAppBuilder.Reports.DependencyInjection;
using QuestionAppBuilder.Reports.Routing;
using QuestionAppBuilder.Reports.ViewModels;

var builder = WebApplication.CreateBuilder(args);
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
    options.LayoutName = "_Layout";
    options.PublicBaseUri = new Uri("https://package-consumer.example.test");
});
builder.Services.AddQuestionAppBuilderReports(options =>
{
    options.BasePath = "/Insights";
    options.ReportPolicy = "PackageReport";
    options.LayoutName = "_Layout";
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PackageDesign", policy => policy.RequireAssertion(_ => true));
    options.AddPolicy("PackageFill", policy => policy.RequireAssertion(_ => true));
    options.AddPolicy("PackageReport", policy => policy.RequireAssertion(_ => true));
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("package-editor-view", "Builder/PackageProof/Editor",
    new { controller = "PackageProof", action = "Editor" });
app.MapControllerRoute("package-reports-view", "Insights/PackageProof/Reports",
    new { controller = "PackageProof", action = "Reports" });
app.MapQuestionAppBuilderEditor();
app.MapQuestionAppBuilderRuntime();
app.MapQuestionAppBuilderReports();
app.Run();

public sealed class PackageProofController : Controller
{
    [HttpGet]
    public IActionResult Editor()
    {
        ViewData["QuestionAppBuilderLayout"] = "_Layout";
        return View("~/Areas/QuestionAppBuilderEditor/Views/Applications/Index.cshtml",
            Array.Empty<ApplicationRowViewModel>());
    }

    [HttpGet]
    public IActionResult Reports()
    {
        var overview = new ReportOverview(0, 0, null, Array.Empty<ReportApplicationOption>(), Array.Empty<VersionScoreDistribution>());
        var filter = new ReportFilterViewModel(null, null, null, null, null);
        return View("~/Areas/QuestionAppBuilderReports/Views/Reports/Index.cshtml", new ReportIndexViewModel(overview, filter));
    }
}
