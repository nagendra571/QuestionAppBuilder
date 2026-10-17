using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class ApplicationTypeManagementTests : IDisposable
{
    private readonly EditorWebApplicationFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Designer_can_add_custom_type_and_use_it_for_a_new_application()
    {
        await EnsureDatabaseAsync();
        var designer = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(designer, "/ConfigureApp/ApplicationTypes");
        var response = await designer.PostAsync("/ConfigureApp/ApplicationTypes/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = token, ["NewName"] = "Community grant" }));
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var applicationTypeId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<IApplicationDesignService>().ListApplicationTypesAsync()).Single(t => t.Name == "Community grant").Id);
        var create = await designer.GetStringAsync("/ConfigureApp/Applications/Create");
        create.Should().Contain("Community grant");
        applicationTypeId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Duplicate_type_name_is_rejected_case_insensitively()
    {
        await EnsureDatabaseAsync();
        var designer = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(designer, "/ConfigureApp/ApplicationTypes");
        var response = await designer.PostAsync("/ConfigureApp/ApplicationTypes/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = token, ["NewName"] = "request for application (rfa)" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("already exists");
        var matchingTypes = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<IApplicationDesignService>().ListApplicationTypesAsync())
                .Count(t => t.Name.Equals("request for application (rfa)", StringComparison.OrdinalIgnoreCase)));
        matchingTypes.Should().Be(1);
    }

    private Task EnsureDatabaseAsync()
        => _factory.WithScopeAsync(async services => { await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync(); return true; });
}
