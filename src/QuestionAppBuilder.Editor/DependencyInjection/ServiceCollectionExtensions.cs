using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuestionAppBuilder.Core.Rendering;
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
        if (string.IsNullOrWhiteSpace(options.DesignPolicy))
            throw new InvalidOperationException("QuestionAppBuilderEditorOptions.DesignPolicy must name a configured host policy.");

        services.AddSingleton(options);
        services.AddScoped<EditorAuthorizationFilter>();
        services.AddAntiforgery(antiforgery => antiforgery.HeaderName = "RequestVerificationToken");
        services.TryAddSingleton<IQuestionnaireRenderer, QuestionnaireRenderModelFactory>();
        services.AddControllersWithViews().AddApplicationPart(typeof(ApplicationsController).Assembly);

        return services;
    }
}
