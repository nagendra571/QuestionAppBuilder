namespace QuestionAppBuilder.Application.Access;

public sealed class ResponseAccessOptions
{
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan GrantLifetime { get; set; } = TimeSpan.FromHours(8);
    public int MaxRequestsPerWindow { get; set; } = 5;
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(15);
}
