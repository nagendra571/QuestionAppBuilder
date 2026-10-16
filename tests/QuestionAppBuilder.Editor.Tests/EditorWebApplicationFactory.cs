using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.SampleHost.DevAuth;

namespace QuestionAppBuilder.Editor.Tests;

public class EditorWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var databaseName = $"editor-tests-{Guid.NewGuid():N}";

        builder.ConfigureServices(services =>
        {
            var dbContextOptions = services.Single(descriptor => descriptor.ServiceType == typeof(DbContextOptions<QuestionAppBuilderDbContext>));
            services.Remove(dbContextOptions);
            services.AddDbContext<QuestionAppBuilderDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
        });
    }

    public async Task<HttpClient> CreateSignedInClientAsync(string username, bool allowAutoRedirect = false)
    {
        ArgumentNullException.ThrowIfNull(username);

        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
        var (token, _) = await GetFormAsync(client, "/Account/Login");

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = DevUserStore.SharedPassword,
            ["__RequestVerificationToken"] = token,
        }));

        if (allowAutoRedirect)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Found);
        }

        return client;
    }

    public async Task<(string Token, string Html)> GetFormAsync(HttpClient client, string url)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);

        var html = await client.GetStringAsync(url);
        var match = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue($"the form at {url} should include an antiforgery token");
        return (WebUtility.HtmlDecode(match.Groups[1].Value), html);
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }
}
