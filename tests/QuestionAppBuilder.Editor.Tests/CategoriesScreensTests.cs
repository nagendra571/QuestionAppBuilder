using System.Globalization;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class CategoriesScreensTests : IDisposable
{
    private const string AddCategoryUrl = "/ConfigureApp/Categories/Add";

    private readonly EditorWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Detail_shows_draft_editable_for_new_application()
    {
        var applicationId = await CreateApplicationAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");

        var response = await client.GetAsync(DetailUrl(applicationId));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Draft");
        body.Should().Contain("Add category");
        body.Should().NotContain("Start editing");
    }

    [Fact]
    public async Task Detail_shows_published_readonly_when_no_draft()
    {
        var (applicationId, _) = await CreatePublishedApplicationAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");

        var response = await client.GetAsync(DetailUrl(applicationId));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Published");
        body.Should().Contain("Start editing");
        body.Should().NotContain("Add category");
        body.Should().Contain("Site Information");
    }

    [Fact]
    public async Task StartDraft_clones_and_unlocks_editing()
    {
        var (applicationId, _) = await CreatePublishedApplicationAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            StartDraftUrl(applicationId),
            BuildStartDraftForm(token));

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var body = await client.GetStringAsync(DetailUrl(applicationId));
        body.Should().Contain("Draft");
        body.Should().Contain("Add category");
        body.Should().Contain("Site Information");
    }

    [Fact]
    public async Task StartDraft_twice_creates_exactly_one_draft()
    {
        var (applicationId, _) = await CreatePublishedApplicationAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");

        var (firstToken, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        var first = await client.PostAsync(StartDraftUrl(applicationId), BuildStartDraftForm(firstToken));
        first.StatusCode.Should().Be(HttpStatusCode.Found);
        var firstDraftVersionId = await GetDraftVersionIdAsync(applicationId);

        var (secondToken, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        var second = await client.PostAsync(StartDraftUrl(applicationId), BuildStartDraftForm(secondToken));
        second.StatusCode.Should().Be(HttpStatusCode.Found);
        var secondDraftVersionId = await GetDraftVersionIdAsync(applicationId);

        secondDraftVersionId.Should().Be(firstDraftVersionId);

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var versionCount = await database.ApplicationVersions.CountAsync(v => v.ApplicationId == applicationId);

            versionCount.Should().Be(2);
            return true;
        });
    }

    [Fact]
    public async Task Add_category_appears_in_detail()
    {
        var applicationId = await CreateApplicationAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddCategoryUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Name"] = "General",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        var location = response.Headers.Location!.ToString();
        location.Should().Contain($"/Detail/{applicationId}");

        var body = await client.GetStringAsync(location);
        body.Should().Contain("General");
        body.Should().NotContain("Independent");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var category = await database.Categories.SingleAsync(c => c.Name == "General");

            category.LogicalKey.Should().NotBeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task Rename_category_updates_detail()
    {
        var applicationId = await CreateApplicationAsync();
        var categoryId = await AddCategoryAsync(applicationId, "General");
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var updateResponse = await client.PostAsync(
            UpdateCategoryUrl(categoryId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Name"] = "Site Information",
                ["__RequestVerificationToken"] = token,
            }));

        updateResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var detailAfterRename = await client.GetStringAsync(DetailUrl(applicationId));
        detailAfterRename.Should().Contain("Site Information");
        detailAfterRename.Should().NotContain("General");
        detailAfterRename.Should().NotContain("Independent");

        var (removeToken, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        var removeResponse = await client.PostAsync(
            RemoveCategoryUrl(categoryId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = removeToken,
            }));

        removeResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var detailAfterRemove = await client.GetStringAsync(DetailUrl(applicationId));
        detailAfterRemove.Should().NotContain("Site Information");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var categoryCount = await database.Categories.CountAsync(c => c.Id == categoryId);

            categoryCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Category_validation_errors_are_friendly()
    {
        var applicationId = await CreateApplicationAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var overLongResponse = await client.PostAsync(
            AddCategoryUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Name"] = new string('x', 201),
                ["__RequestVerificationToken"] = token,
            }));

        overLongResponse.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        overLongResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        var overLongBody = await client.GetStringAsync(overLongResponse.Headers.Location!.ToString());
        overLongBody.Should().Contain("at most 200");

        var (blankToken, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        var blankResponse = await client.PostAsync(
            AddCategoryUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Name"] = string.Empty,
                ["__RequestVerificationToken"] = blankToken,
            }));

        blankResponse.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        blankResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        var blankBody = await client.GetStringAsync(blankResponse.Headers.Location!.ToString());
        blankBody.Should().Contain("required");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var categoryCount = await database.Categories.CountAsync();

            categoryCount.Should().Be(0);
            return true;
        });
    }

    private static string DetailUrl(int applicationId)
        => $"/ConfigureApp/Applications/Detail/{applicationId}";

    private static string StartDraftUrl(int applicationId)
        => $"/ConfigureApp/Applications/StartDraft/{applicationId}";

    private static string UpdateCategoryUrl(int categoryId)
        => $"/ConfigureApp/Categories/Update/{categoryId}";

    private static string RemoveCategoryUrl(int categoryId)
        => $"/ConfigureApp/Categories/Remove/{categoryId}";

    private static FormUrlEncodedContent BuildStartDraftForm(string token)
        => new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        });

    private async Task<int> CreateApplicationAsync()
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = await SeedAndGetDesignServiceAsync(services);
            var application = await designService.CreateApplicationAsync("Endowment Expansion", await GetRfaTypeIdAsync(designService));
            return application.Id;
        });

    private async Task<(int ApplicationId, int CategoryId)> CreatePublishedApplicationAsync()
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = await SeedAndGetDesignServiceAsync(services);
            var application = await designService.CreateApplicationAsync("Endowment Expansion", await GetRfaTypeIdAsync(designService));
            var category = await designService.AddCategoryAsync(application.Id, "Site Information");
            await designService.AddQuestionAsync(category.Id, "Licensed infant capacity", AnswerType.Number);
            await designService.PublishAsync(application.Id);
            return (application.Id, category.Id);
        });

    private async Task<int> AddCategoryAsync(int applicationId, string name)
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var category = await designService.AddCategoryAsync(applicationId, name);
            return category.Id;
        });

    private async Task<int> GetDraftVersionIdAsync(int applicationId)
        => await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var draft = await database.ApplicationVersions
                .SingleAsync(v => v.ApplicationId == applicationId && v.Status == VersionStatus.Draft);

            return draft.Id;
        });

    private static async Task<IApplicationDesignService> SeedAndGetDesignServiceAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
        await database.Database.EnsureCreatedAsync();
        return services.GetRequiredService<IApplicationDesignService>();
    }

    private static async Task<int> GetRfaTypeIdAsync(IApplicationDesignService designService)
    {
        var types = await designService.ListApplicationTypesAsync();
        return types.Single(t => t.Name.Contains("RFA", StringComparison.Ordinal)).Id;
    }
}
