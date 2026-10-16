using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Editor.Controllers;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuestionAppBuilderEditor(
        this IServiceCollection services,
        Action<QuestionAppBuilderEditorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new QuestionAppBuilderEditorOptions();
        configure?.Invoke(options);

        if (!options.BasePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "QuestionAppBuilderEditorOptions.BasePath must start with '/'.");
        }

        services.AddSingleton(options);
        services.AddControllersWithViews().AddApplicationPart(typeof(ApplicationsController).Assembly);

        return services;
    }
}
