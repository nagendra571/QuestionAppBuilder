using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class ApplicationsScreensTests : IDisposable
{
    private const string CreateUrl = "/ConfigureApp/Applications/Create";
    private const string IndexUrl = "/ConfigureApp/Applications";

    private readonly EditorWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_form_lists_seeded_types()
    {
        await SeedApplicationTypesAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");

        var response = await client.GetAsync(CreateUrl);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("RFA");
        body.Should().Contain("RFI");
    }

    [Fact]
    public async Task Create_posts_and_redirects_to_new_application()
    {
        await SeedApplicationTypesAsync();
        var applicationTypeId = await GetApplicationTypeIdAsync("RFA");
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, CreateUrl);

        var response = await client.PostAsync(
            CreateUrl,
            BuildCreateForm("Endowment Expansion", applicationTypeId, token));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        var location = response.Headers.Location!;
        var locationPath = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var match = Regex.Match(locationPath, "^/ConfigureApp/Applications/Detail/(?<id>\\d+)$");
        match.Success.Should().BeTrue($"the redirect should point at the new application, but was {locationPath}");
        var newApplicationId = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var application = await database.Applications.SingleOrDefaultAsync(a => a.Id == newApplicationId);

            application.Should().NotBeNull();
            application!.Name.Should().Be("Endowment Expansion");
            application.ApplicationTypeId.Should().Be(applicationTypeId);
            return true;
        });
    }

    [Fact]
    public async Task Duplicate_name_is_model_state_error()
    {
        await SeedApplicationTypesAsync();
        var applicationTypeId = await GetApplicationTypeIdAsync("RFA");
        await CreateApplicationAsync("Alpha", applicationTypeId);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, CreateUrl);

        var response = await client.PostAsync(
            CreateUrl,
            BuildCreateForm("Alpha", applicationTypeId, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        WebUtility.HtmlDecode(body).Should().Contain("An application named 'Alpha' already exists.");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var alphaCount = await database.Applications.CountAsync(a => a.Name == "Alpha");

            alphaCount.Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task Over_long_name_is_model_state_error()
    {
        await SeedApplicationTypesAsync();
        var applicationTypeId = await GetApplicationTypeIdAsync("RFA");
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, CreateUrl);

        var response = await client.PostAsync(
            CreateUrl,
            BuildCreateForm(new string('x', 201), applicationTypeId, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("at most 200");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var applicationCount = await database.Applications.CountAsync();

            applicationCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Index_encodes_hostile_names()
    {
        await SeedApplicationTypesAsync();
        var applicationTypeId = await GetApplicationTypeIdAsync("RFA");
        await CreateApplicationAsync("<b>bold</b>", applicationTypeId);
        var client = await _factory.CreateSignedInClientAsync("designer");

        var body = await client.GetStringAsync(IndexUrl);

        body.Should().Contain("&lt;b&gt;bold&lt;/b&gt;");
        body.Should().NotContain("<b>bold</b>");
    }

    [Fact]
    public async Task Index_shows_application_type_name()
    {
        await SeedApplicationTypesAsync();
        var applicationTypeId = await GetApplicationTypeIdAsync("RFA");
        await CreateApplicationAsync("Endowment Expansion", applicationTypeId);
        var client = await _factory.CreateSignedInClientAsync("designer");

        var body = await client.GetStringAsync(IndexUrl);

        var row = Regex.Match(body, "<tr[^>]*>.*?Endowment Expansion.*?</tr>", RegexOptions.Singleline);
        row.Success.Should().BeTrue("the application should appear in an index row");
        row.Value.Should().Contain("RFA");
    }

    private async Task SeedApplicationTypesAsync()
    {
        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            await database.Database.EnsureCreatedAsync();
            return true;
        });
    }

    private async Task<int> GetApplicationTypeIdAsync(string nameFragment)
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var types = await designService.ListApplicationTypesAsync();
            return types.Single(t => t.Name.Contains(nameFragment, StringComparison.Ordinal)).Id;
        });

    private async Task CreateApplicationAsync(string name, int applicationTypeId)
    {
        await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            await designService.CreateApplicationAsync(name, applicationTypeId);
            return true;
        });
    }

    private static FormUrlEncodedContent BuildCreateForm(string name, int applicationTypeId, string token)
        => new(new Dictionary<string, string>
        {
            ["Name"] = name,
            ["ApplicationTypeId"] = applicationTypeId.ToString(CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = token,
        });
}
