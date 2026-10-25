using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class WorkspaceSearchAuthorizationTests
{
    [Fact]
    public async Task Search_is_scoped_to_one_application_and_does_not_change_its_revision()
    {
        using var factory = new EditorWebApplicationFactory();
        var seeded = await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var first = await design.CreateApplicationAsync("Search scope " + Guid.NewGuid(), type.Id);
            var category = await design.AddCategoryAsync(first.Id, "Scope category");
            await design.AddQuestionAsync(category.Id, "Searchable local question", AnswerType.Textbox, false);
            var other = await design.CreateApplicationAsync("Search other " + Guid.NewGuid(), type.Id);
            var otherCategory = await design.AddCategoryAsync(other.Id, "Other category");
            await design.AddQuestionAsync(otherCategory.Id, "Searchable other question", AnswerType.Textbox, false);
            var snapshot = await services.GetRequiredService<IDraftService>().GetAsync(first.Id);
            return (first.Id, snapshot.Revision);
        });
        using var client = await factory.CreateSignedInClientAsync("designer");
        var revision = Uri.EscapeDataString(Convert.ToBase64String(seeded.Revision));

        using var response = await client.GetAsync($"/ConfigureApp/Drafts/{seeded.Id}/Search?q=Searchable&expectedRevision={revision}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Searchable local question").And.NotContain("Searchable other question");
        var current = await factory.WithScopeAsync(async services =>
            await services.GetRequiredService<IDraftService>().GetAsync(seeded.Id));
        current.Revision.Should().Equal(seeded.Revision);
    }

    [Theory]
    [InlineData("/ConfigureApp/Drafts/7/Search?q=A&expectedRevision=AQ%3D%3D")]
    [InlineData("/ConfigureApp/WorkspaceSearch/Search?applicationId=7&q=A&expectedRevision=AQ%3D%3D")]
    public async Task Respondent_cannot_search_designer_configuration(string url)
    {
        using var factory = new EditorWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync("filler");

        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
