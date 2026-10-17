using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class EditorAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IAuthorizationService _authorization;
    private readonly QuestionAppBuilderEditorOptions _options;

    public EditorAuthorizationFilter(IAuthorizationService authorization, QuestionAppBuilderEditorOptions options)
    { _authorization = authorization; _options = options; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var result = await _authorization.AuthorizeAsync(context.HttpContext.User, resource: null, _options.DesignPolicy);
        if (result.Succeeded) return;
        context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
            ? new ForbidResult()
            : new ChallengeResult();
    }
}
