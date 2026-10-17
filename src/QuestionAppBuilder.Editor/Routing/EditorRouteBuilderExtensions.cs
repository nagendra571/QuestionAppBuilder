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

        var preview = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditorPreview",
            pattern: $"{basePath}/Preview/{{applicationId:int}}/Evaluate",
            defaults: new { area = EditorArea.Name, controller = "Preview", action = "Evaluate" });
        var save = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditorDraftSave",
            pattern: $"{basePath}/Drafts/{{applicationId:int}}/Save",
            defaults: new { area = EditorArea.Name, controller = "Drafts", action = "Save" });
        var publish = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditorDraftPublish",
            pattern: $"{basePath}/Drafts/{{applicationId:int}}/Publish",
            defaults: new { area = EditorArea.Name, controller = "Drafts", action = "Publish" });
        var readiness = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditorDraftReadiness",
            pattern: $"{basePath}/Drafts/{{applicationId:int}}/Readiness",
            defaults: new { area = EditorArea.Name, controller = "Drafts", action = "Readiness" });
        var workspace = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditorDraftWorkspace",
            pattern: $"{basePath}/Drafts/{{applicationId:int}}",
            defaults: new { area = EditorArea.Name, controller = "Drafts", action = "Index" });
        var route = endpoints.MapControllerRoute(
            name: "QuestionAppBuilderEditor",
            pattern: $"{basePath}/{{controller=Applications}}/{{action=Index}}/{{id?}}",
            defaults: new { area = EditorArea.Name });

        if (!string.IsNullOrWhiteSpace(options.DesignerRole))
        {
            preview.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
            save.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
            publish.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
            readiness.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
            workspace.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
            route.RequireAuthorization(new AuthorizeAttribute { Roles = options.DesignerRole });
        }

        return endpoints;
    }
}
