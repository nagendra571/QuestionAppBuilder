using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Core.Options;

namespace QuestionAppBuilder.Core.Authorization;

/// <summary>Enforces the host's respondent policy even when a controller is reached through a conventional MVC route.</summary>
public sealed class RespondentFillAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IAuthorizationService _authorization;
    private readonly QuestionAppBuilderRuntimeOptions _options;

    public RespondentFillAuthorizationFilter(IAuthorizationService authorization, IOptions<QuestionAppBuilderRuntimeOptions> options)
    {
        _authorization = authorization;
        _options = options.Value;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.Filters.OfType<IAllowAnonymousFilter>().Any()
            || context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null) return;

        // Using HttpContext as the resource lets a host policy inspect a valid email response grant.
        var result = await _authorization.AuthorizeAsync(context.HttpContext.User, context.HttpContext, _options.FillPolicy);
        if (result.Succeeded) return;

        context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
            ? UseConfiguredScheme ? new ForbidResult(_options.HostAuthenticationScheme) : new ForbidResult()
            : UseConfiguredScheme ? new ChallengeResult(_options.HostAuthenticationScheme) : new ChallengeResult();
    }

    private bool UseConfiguredScheme => !string.IsNullOrWhiteSpace(_options.HostAuthenticationScheme);
}
