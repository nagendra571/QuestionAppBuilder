using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuestionAppBuilder.Reports.Controllers;
using QuestionAppBuilder.Reports.Options;

namespace QuestionAppBuilder.Reports.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuestionAppBuilderReports(this IServiceCollection services,
        Action<QuestionAppBuilderReportsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new QuestionAppBuilderReportsOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.BasePath) || !options.BasePath.StartsWith('/') || options.BasePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Reports BasePath must be a rooted application path.");
        if (string.IsNullOrWhiteSpace(options.ReportPolicy))
            throw new InvalidOperationException("A host report authorization policy is required.");
        if (string.IsNullOrWhiteSpace(options.LayoutName))
            throw new InvalidOperationException("A host layout name is required.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(options.DisplayTimeZone); }
        catch (TimeZoneNotFoundException exception)
        { throw new InvalidOperationException($"Unknown reports display time zone '{options.DisplayTimeZone}'.", exception); }
        catch (InvalidTimeZoneException exception)
        { throw new InvalidOperationException($"Invalid reports display time zone '{options.DisplayTimeZone}'.", exception); }

        services.AddSingleton(options);
        services.AddControllersWithViews().AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddScoped<ReportsAuthorizationFilter>();
        return services;
    }
}
