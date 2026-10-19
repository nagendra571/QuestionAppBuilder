using System.Net;
using FluentAssertions;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class AccountNavigationTests
{
    [Theory]
    [InlineData("designer", "/ConfigureApp/Applications")]
    [InlineData("filler", "/App/Mine")]
    [InlineData("analyst", "/Reports/")]
    public async Task Login_and_root_redirect_to_the_signed_in_roles_surface(string username, string expectedPath)
    {
        using var factory = new EditorWebApplicationFactory();
        using var client = CreateClient(factory);
        var login = await PostLoginAsync(factory, client, username);

        login.StatusCode.Should().Be(HttpStatusCode.Found);
        login.Headers.Location!.OriginalString.Should().Be("/");
        var root = await client.GetAsync(login.Headers.Location);
        root.StatusCode.Should().Be(HttpStatusCode.Found, await root.Content.ReadAsStringAsync());
        root.Headers.Location!.OriginalString.Should().Be(expectedPath);
        var surface = await client.GetAsync(root.Headers.Location);
        surface.StatusCode.Should().Be(HttpStatusCode.OK, await surface.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_and_root_use_configured_surface_paths()
    {
        using var factory = new EditorWebApplicationFactory(new Dictionary<string, string?>
        {
            ["QuestionAppBuilderEditor:BasePath"] = "/admin/design",
            ["QuestionAppBuilderRuntime:BasePath"] = "/forms",
            ["QuestionAppBuilderReports:BasePath"] = "/analytics",
        });
        foreach (var (username, expectedPath) in new[]
        {
            ("designer", "/admin/design/Applications"),
            ("filler", "/forms/Mine"),
            ("analyst", "/analytics/"),
        })
        {
            using var client = CreateClient(factory);
            var login = await PostLoginAsync(factory, client, username);
            login.Headers.Location!.OriginalString.Should().Be("/");
            var root = await client.GetAsync(login.Headers.Location);
            root.Headers.Location!.OriginalString.Should().Be(expectedPath);
            (await client.GetAsync(root.Headers.Location)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Anonymous_root_challenges_and_invalid_login_does_not_sign_in()
    {
        using var factory = new EditorWebApplicationFactory();
        using var client = CreateClient(factory);

        var root = await client.GetAsync("/");
        root.StatusCode.Should().Be(HttpStatusCode.Found);
        new Uri(client.BaseAddress!, root.Headers.Location!).AbsolutePath.Should().Be("/Account/Login");

        var login = await PostLoginAsync(factory, client, "filler", "wrong-password");
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadAsStringAsync()).Should().Contain("Invalid username or password");
        (await client.GetAsync("/ConfigureApp/Applications")).StatusCode.Should().Be(HttpStatusCode.Found);
    }

    [Theory]
    [InlineData("designer", "/ConfigureApp/Applications")]
    [InlineData("filler", "/App/Mine")]
    [InlineData("analyst", "/Reports/")]
    public async Task Logout_returns_to_login_and_removes_surface_authorization(string username, string protectedPath)
    {
        using var factory = new EditorWebApplicationFactory();
        using var client = CreateClient(factory);
        await PostLoginAsync(factory, client, username);
        var form = await factory.GetFormAsync(client, protectedPath);
        var logout = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = form.Token,
        }));

        logout.StatusCode.Should().Be(HttpStatusCode.Found);
        logout.Headers.Location!.OriginalString.Should().Be("/Account/Login");
        (await client.GetAsync(logout.Headers.Location)).StatusCode.Should().Be(HttpStatusCode.OK);
        var formerSurface = await client.GetAsync(protectedPath);
        formerSurface.StatusCode.Should().Be(HttpStatusCode.Found);
        new Uri(client.BaseAddress!, formerSurface.Headers.Location!).AbsolutePath.Should().Be("/Account/Login");
    }

    [Fact]
    public async Task Filler_and_analyst_remain_forbidden_from_designer_routes()
    {
        using var factory = new EditorWebApplicationFactory();
        foreach (var username in new[] { "filler", "analyst" })
        {
            using var client = await factory.CreateSignedInClientAsync(username);
            (await client.GetAsync("/ConfigureApp/Applications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    private static HttpClient CreateClient(EditorWebApplicationFactory factory)
        => factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<HttpResponseMessage> PostLoginAsync(EditorWebApplicationFactory factory, HttpClient client,
        string username, string password = "DevPassword!1")
    {
        var form = await factory.GetFormAsync(client, "/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
            ["__RequestVerificationToken"] = form.Token,
        }));
    }
}
