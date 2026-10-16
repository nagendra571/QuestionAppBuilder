using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Routing;

public static class EditorRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapQuestionAppBuilderEditor(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<QuestionAppBuilderEditorOptions>();
        var basePath = options.BasePath.Trim('/');

        var route = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditor",
            pattern: $"{basePath}/{{controller=Applications}}/{{action=Index}}/{{id?}}",
            defaults: new { area = EditorArea.Name });

        if (!string.IsNullOrWhiteSpace(options.DesignerRole))
        {
            route.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
        }

        return endpoints;
    }
}
