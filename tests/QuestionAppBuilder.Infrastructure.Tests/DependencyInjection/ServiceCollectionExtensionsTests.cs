using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Infrastructure.DependencyInjection;

namespace QuestionAppBuilder.Infrastructure.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Missing_connection_string_throws()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddQuestionAppBuilder(_ => { });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Owned_coordinator_is_registered_and_legacy_unowned_submitter_is_not()
    {
        var services = new ServiceCollection();
        services.AddQuestionAppBuilder(o => o.ConnectionString = "Server=unused");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISubmissionCoordinator>().Should().NotBeNull();
        services.Should().NotContain(descriptor => descriptor.ServiceType.FullName == "QuestionAppBuilder.Application.Services.ISubmissionService");
    }
}
