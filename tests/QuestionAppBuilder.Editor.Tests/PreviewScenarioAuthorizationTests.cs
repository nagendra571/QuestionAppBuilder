using System.Net;
using System.Text;
using FluentAssertions;

namespace QuestionAppBuilder.Editor.Tests;

public class PreviewScenarioAuthorizationTests
{
    [Fact]
    public async Task Respondent_cannot_read_or_run_shared_designer_scenarios()
    {
        using var factory = new EditorWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync("filler");

        using var listed = await client.GetAsync("/ConfigureApp/PreviewScenarios/List/7");
        using var run = await client.PostAsync("/ConfigureApp/PreviewScenarios/Run/7", RunRequest());

        listed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        run.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Designer_scenario_posts_require_antiforgery_even_for_read_only_runs()
    {
        using var factory = new EditorWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync("designer");

        using var run = await client.PostAsync("/ConfigureApp/PreviewScenarios/Run/7", RunRequest());

        run.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static StringContent RunRequest() => new(
        """{"draft":{"applicationId":7,"expectedVersionId":11,"expectedRevision":"AQ=="}}""",
        Encoding.UTF8, "application/json");
}
