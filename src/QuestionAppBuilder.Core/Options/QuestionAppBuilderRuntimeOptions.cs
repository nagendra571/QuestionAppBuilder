namespace QuestionAppBuilder.Core.Options;

public sealed class QuestionAppBuilderRuntimeOptions
{
    public string FillPolicy { get; set; } = string.Empty;
    public string HostAuthenticationScheme { get; set; } = string.Empty;
    public string? TrustedIssuer { get; set; }
    public string BasePath { get; set; } = "/App";
    public string LayoutName { get; set; } = "_Layout";
    public TimeSpan EmailLinkLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public Uri? PublicBaseUri { get; set; }
    public int AccessRequestsPerIp { get; set; } = 10;
    public TimeSpan AccessRequestIpWindow { get; set; } = TimeSpan.FromMinutes(15);
}
