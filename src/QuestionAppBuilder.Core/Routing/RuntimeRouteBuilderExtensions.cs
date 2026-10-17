using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Core.Options;

namespace QuestionAppBuilder.Core.Routing;

public static class RuntimeRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapQuestionAppBuilderRuntime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<QuestionAppBuilderRuntimeOptions>>().Value;
        var prefix = options.BasePath.Trim('/');
        endpoints.MapControllerRoute("qab-runtime-start", $"{prefix}/{{applicationId:int}}/Start",
            new { area = "QuestionAppBuilderCore", controller = "Responses", action = "Start" }).RequireAuthorization(options.FillPolicy);
        endpoints.MapControllerRoute("qab-runtime-landing", $"{prefix}/{{applicationId:int}}",
            new { area = "QuestionAppBuilderCore", controller = "Responses", action = "Landing" });
        endpoints.MapControllerRoute("qab-runtime-mine", $"{prefix}/Mine",
            new { area = "QuestionAppBuilderCore", controller = "Responses", action = "Mine" }).RequireAuthorization(options.FillPolicy);
        endpoints.MapControllerRoute("qab-runtime-response-action", $"{prefix}/Responses/{{responseId:guid}}/{{action}}",
            new { area = "QuestionAppBuilderCore", controller = "Responses" }).RequireAuthorization(options.FillPolicy);
        endpoints.MapControllerRoute("qab-runtime-response-edit", $"{prefix}/Responses/{{responseId:guid}}",
            new { area = "QuestionAppBuilderCore", controller = "Responses", action = "Edit" }).RequireAuthorization(options.FillPolicy);
        endpoints.MapControllerRoute("qab-runtime-access", $"{prefix}/Access/{{action=Confirm}}", new { controller = "Access" });
        return endpoints;
    }
}
