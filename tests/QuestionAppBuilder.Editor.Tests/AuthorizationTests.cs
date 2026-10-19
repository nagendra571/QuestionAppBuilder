using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace QuestionAppBuilder.Editor.Tests;

public class AuthorizationTests : IClassFixture<EditorWebApplicationFactory>
{
    private readonly EditorWebApplicationFactory _factory;

    public AuthorizationTests(EditorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_user_is_redirected_to_login()
    {
        var client = CreateAnonymousClient();

        var response = await client.GetAsync("/ConfigureApp/Applications");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.AbsolutePath.Should().StartWith("/Account/Login");
    }

    [Fact]
    public async Task Non_designer_role_gets_forbidden()
    {
        var client = await _factory.CreateSignedInClientAsync("filler");

        var response = await client.GetAsync("/ConfigureApp/Applications");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Designer_reaches_applications_index()
    {
        var client = await _factory.CreateSignedInClientAsync("designer");

        var response = await client.GetAsync("/ConfigureApp/Applications");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Applications");
    }

    [Fact]
    public async Task Anonymous_root_challenges_to_login()
    {
        var client = CreateAnonymousClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.AbsolutePath.Should().Be("/Account/Login");
    }

    private HttpClient CreateAnonymousClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
}
