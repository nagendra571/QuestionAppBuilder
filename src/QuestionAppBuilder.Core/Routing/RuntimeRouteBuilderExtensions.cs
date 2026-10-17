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
        endpoints.MapControllerRoute("qab-runtime-access", $"{options.BasePath.Trim('/')}/Access/{{action=Confirm}}", new { controller = "Access" });
        return endpoints;
    }
}
