using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.SampleHost.DevAuth;

namespace QuestionAppBuilder.Editor.Tests;

public class EditorWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _configuration;

    public EditorWebApplicationFactory()
        : this(new Dictionary<string, string?>())
    {
    }

    internal EditorWebApplicationFactory(IReadOnlyDictionary<string, string?> configuration)
        => _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var databaseName = $"editor-tests-{Guid.NewGuid():N}";
        foreach (var (key, value) in _configuration)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_configuration));

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

        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect, BaseAddress = new Uri("https://localhost") });
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

        using var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Form request {url} returned {(int)response.StatusCode}: {html}");
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
