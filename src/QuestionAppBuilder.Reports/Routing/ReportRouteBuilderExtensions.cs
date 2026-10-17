using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Reports.Options;

namespace QuestionAppBuilder.Reports.Routing;

public static class ReportRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapQuestionAppBuilderReports(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<QuestionAppBuilderReportsOptions>();
        var basePath = options.BasePath.TrimEnd('/');
        var pattern = $"{basePath}/{{action=Index}}/{{id?}}";
        endpoints.MapControllerRoute("questionappbuilder-reports", pattern,
                new { area = "QuestionAppBuilderReports", controller = "Reports", action = "Index" },
                new { area = "QuestionAppBuilderReports" })
            .RequireAuthorization(options.ReportPolicy);
        endpoints.MapControllerRoute("questionappbuilder-reports-export", $"{basePath}/Export",
                new { area = "QuestionAppBuilderReports", controller = "Exports", action = "Export" },
                new { area = "QuestionAppBuilderReports" })
            .RequireAuthorization(options.ReportPolicy);
        return endpoints;
    }
}
