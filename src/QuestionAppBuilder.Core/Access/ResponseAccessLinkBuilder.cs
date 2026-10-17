using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Core.Options;

namespace QuestionAppBuilder.Core.Access;

public sealed class ResponseAccessLinkBuilder : IResponseAccessLinkBuilder
{
    private readonly QuestionAppBuilderRuntimeOptions _options;
    public ResponseAccessLinkBuilder(IOptions<QuestionAppBuilderRuntimeOptions> options) => _options = options.Value;

    public Uri CreateLink(string token)
    {
        var origin = _options.PublicBaseUri;
        if (origin is null || !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(origin.UserInfo))
            throw new InvalidOperationException("A trusted HTTPS PublicBaseUri must be configured before sending response access links.");
        var path = $"{_options.BasePath.Trim('/')}/Access/Confirm";
        var uri = new UriBuilder(origin) { Path = $"/{path}" }.Uri;
        return new Uri(QueryHelpers.AddQueryString(uri.ToString(), "token", token));
    }
}
