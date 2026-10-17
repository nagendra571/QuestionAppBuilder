using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Options;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Authoring;
using QuestionAppBuilder.Infrastructure.Responses;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuestionAppBuilder.Infrastructure.Repositories;

namespace QuestionAppBuilder.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuestionAppBuilder(
        this IServiceCollection services,
        Action<QuestionAppBuilderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new QuestionAppBuilderOptions { ConnectionString = string.Empty };
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "A connection string is required. Set QuestionAppBuilderOptions.ConnectionString in the configure action.");
        }

        services.AddSingleton(options);
        services.AddDbContextFactory<QuestionAppBuilderDbContext>(db => db.UseSqlServer(options.ConnectionString), ServiceLifetime.Scoped);
        services.AddScoped<IApplicationRepository, ApplicationRepository>();
        services.AddScoped<ISubmissionRepository, SubmissionRepository>();
        services.AddSingleton<IScoringEngine, ScoringEngine>();
        services.AddSingleton<IDefinitionEvaluator, DefinitionEvaluator>();
        services.AddScoped<IApplicationDesignService, ApplicationDesignService>();
        services.AddScoped<IDraftService, DraftService>();
        services.TryAddScoped<IRespondentContext, UnconfiguredRespondentContext>();
        services.AddScoped<IResponseService, ResponseService>();
        services.AddScoped<ISubmissionCoordinator, SubmissionCoordinator>();
        services.AddScoped<IAvailabilityService, AvailabilityService>();
        services.TryAddScoped<ITrustedActorContext, UnconfiguredTrustedActorContext>();

        return services;
    }
}
