using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QuestionAppBuilder.Reports.Options;

namespace QuestionAppBuilder.Reports.Controllers;

[Area("QuestionAppBuilderReports")]
[ServiceFilter(typeof(ReportsAuthorizationFilter))]
public abstract class ReportControllerBase : Controller
{
}

public sealed class ReportsAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IAuthorizationService _authorization;
    private readonly QuestionAppBuilderReportsOptions _options;

    public ReportsAuthorizationFilter(IAuthorizationService authorization, QuestionAppBuilderReportsOptions options)
    {
        _authorization = authorization;
        _options = options;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var result = await _authorization.AuthorizeAsync(context.HttpContext.User, resource: null, _options.ReportPolicy);
        if (!result.Succeeded)
            context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
                ? new ForbidResult()
                : new ChallengeResult();
    }
}
