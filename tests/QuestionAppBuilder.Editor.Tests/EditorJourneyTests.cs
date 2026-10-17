using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class EditorJourneyTests : IDisposable
{
    private const string BasePath = "/ConfigureApp";
    private const string QuestionLabel = "Has the site received Early Start CT funding in the last 12 months?";

    private readonly EditorWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Full_designer_journey()
    {
        await SeedDatabaseAsync(_factory);
        var applicationTypeId = await GetApplicationTypeIdAsync(_factory);
        var client = await _factory.CreateSignedInClientAsync("designer");

        var (createToken, _) = await _factory.GetFormAsync(client, CreateUrl);
        var createResponse = await client.PostAsync(
            CreateUrl,
            BuildCreateForm("Endowment Expansion", applicationTypeId, createToken));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        createResponse.Headers.Location.Should().NotBeNull();
        var applicationId = ParseDetailId(createResponse.Headers.Location!);

        var addCategoryResponse = await client.PostAsync(
            $"{BasePath}/Categories/Add",
            BuildForm(
                await GetDetailTokenAsync(client, applicationId),
                ("applicationId", Invariant(applicationId)),
                ("Name", "Site Information")));
        addCategoryResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var versionOneId = await GetDraftVersionIdAsync(applicationId);
        var categoryId = await GetCategoryIdAsync(versionOneId, "Site Information");

        var addQuestionResponse = await client.PostAsync(
            $"{BasePath}/Elements/AddQuestion/{categoryId}",
            BuildForm(
                await GetDetailTokenAsync(client, applicationId),
                ("applicationId", Invariant(applicationId)),
                ("Label", QuestionLabel),
                ("AnswerType", nameof(AnswerType.RadioGroup)),
                ("IsRequired", "true")));
        addQuestionResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var questionId = await GetQuestionIdAsync(categoryId, QuestionLabel);

        var firstOptionResponse = await client.PostAsync(
            $"{BasePath}/Options/Add/{questionId}",
            BuildForm(
                await GetDetailTokenAsync(client, applicationId),
                ("applicationId", Invariant(applicationId)),
                ("Label", "Yes - previously funded"),
                ("Value", "yes")));
        firstOptionResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var secondOptionResponse = await client.PostAsync(
            $"{BasePath}/Options/Add/{questionId}",
            BuildForm(
                await GetDetailTokenAsync(client, applicationId),
                ("applicationId", Invariant(applicationId)),
                ("Label", "No - first application"),
                ("Value", "no")));
        secondOptionResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var optionIds = await GetOptionIdsAsync(questionId);
        optionIds.Should().HaveCount(2);

        var addRuleResponse = await client.PostAsync(
            $"{BasePath}/Rules/Add/{questionId}",
            BuildForm(
                await GetDetailTokenAsync(client, applicationId),
                ("applicationId", Invariant(applicationId)),
                ("Family", nameof(RuleFamily.Selection)),
                ("Points", "5"),
                ("MembershipOperator", nameof(MembershipOperator.AnyOf)),
                ("OptionIds", Invariant(optionIds[0])),
                ("OptionIds", Invariant(optionIds[1]))));
        addRuleResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var ruleHtml = await client.GetStringAsync(DetailUrl(applicationId));
        ruleHtml.Should().Contain("Scoring rule");
        ruleHtml.Should().Contain("+5 points");

        var (publishToken, draftHtml) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        draftHtml.Should().Contain("Publish version 1");

        var publishResponse = await client.PostAsync(
            $"{BasePath}/Applications/Publish/{applicationId}",
            BuildForm(publishToken, ("expectedRevision", ExtractRevision(draftHtml))));
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        publishResponse.Headers.Location.Should().NotBeNull();
        publishResponse.Headers.Location!.ToString().Should().Contain($"/Applications/Detail/{applicationId}");

        var publishedHtml = await client.GetStringAsync(DetailUrl(applicationId));
        publishedHtml.Should().Contain("Published version 1");
        publishedHtml.Should().Contain("published and read-only");
        publishedHtml.Should().Contain("Start editing");
        publishedHtml.Should().NotContain("Add category");
        publishedHtml.Should().NotContain("Publish version");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var version = await database.ApplicationVersions.SingleAsync(v => v.Id == versionOneId);
            var application = await database.Applications.SingleAsync(a => a.Id == applicationId);

            version.Status.Should().Be(VersionStatus.Published);
            version.PublishedUtc.Should().NotBeNull();
            application.CurrentVersionId.Should().Be(versionOneId);
            return true;
        });

        var startDraftResponse = await client.PostAsync(
            $"{BasePath}/Applications/StartDraft/{applicationId}",
            BuildForm(await GetDetailTokenAsync(client, applicationId)));
        startDraftResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var draftVersionId = await GetDraftVersionIdAsync(applicationId);
        draftVersionId.Should().NotBe(versionOneId);
        var draftCategoryId = await GetCategoryIdAsync(draftVersionId, "Site Information");
        draftCategoryId.Should().NotBe(categoryId);

        var draftCounts = await GetVersionContentCountsAsync(draftVersionId);
        draftCounts.Categories.Should().Be(1);
        draftCounts.Questions.Should().Be(1);
        draftCounts.Options.Should().Be(2);
        draftCounts.Rules.Should().Be(1);

        var draftBody = await client.GetStringAsync(DetailUrl(applicationId));
        draftBody.Should().Contain("Draft version 2");
        draftBody.Should().Contain("Published v1 exists");
        draftBody.Should().Contain(QuestionLabel);
        draftBody.Should().Contain("Publish version 2");

        var addSecondCategoryResponse = await client.PostAsync(
            $"{BasePath}/Categories/Add",
            BuildForm(
                await GetDetailTokenAsync(client, applicationId),
                ("applicationId", Invariant(applicationId)),
                ("Name", "Space Types")));
        addSecondCategoryResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var (secondPublishToken, secondPublishHtml) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        var secondPublishResponse = await client.PostAsync(
            $"{BasePath}/Applications/Publish/{applicationId}",
            BuildForm(secondPublishToken, ("expectedRevision", ExtractRevision(secondPublishHtml))));
        secondPublishResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var republishedHtml = await client.GetStringAsync(DetailUrl(applicationId));
        republishedHtml.Should().Contain("Published version 2");
        republishedHtml.Should().Contain("Space Types");
        republishedHtml.Should().NotContain("Add category");
        republishedHtml.Should().NotContain("Publish version");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var publishedVersionOne = await database.ApplicationVersions.SingleAsync(v => v.Id == versionOneId);
            var publishedVersionTwo = await database.ApplicationVersions.SingleAsync(v => v.Id == draftVersionId);
            var application = await database.Applications.SingleAsync(a => a.Id == applicationId);
            var versionOneCategoryCount = await database.Categories.CountAsync(c => c.ApplicationVersionId == versionOneId);
            var versionTwoCategoryCount = await database.Categories.CountAsync(c => c.ApplicationVersionId == draftVersionId);

            publishedVersionOne.Status.Should().Be(VersionStatus.Published);
            publishedVersionTwo.Status.Should().Be(VersionStatus.Published);
            publishedVersionTwo.PublishedUtc.Should().NotBeNull();
            application.CurrentVersionId.Should().Be(draftVersionId);
            versionOneCategoryCount.Should().Be(1);
            versionTwoCategoryCount.Should().Be(2);
            return true;
        });
    }

    [Fact]
    public async Task Publish_without_content_is_friendly_error()
    {
        await SeedDatabaseAsync(_factory);
        var applicationTypeId = await GetApplicationTypeIdAsync(_factory);
        var client = await _factory.CreateSignedInClientAsync("designer");

        var (createToken, _) = await _factory.GetFormAsync(client, CreateUrl);
        var createResponse = await client.PostAsync(
            CreateUrl,
            BuildCreateForm("Endowment Expansion", applicationTypeId, createToken));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        var applicationId = ParseDetailId(createResponse.Headers.Location!);

        var (publishToken, publishHtml) = await _factory.GetFormAsync(client, DetailUrl(applicationId));
        var publishResponse = await client.PostAsync(
            $"{BasePath}/Applications/Publish/{applicationId}",
            BuildForm(publishToken, ("expectedRevision", ExtractRevision(publishHtml))));

        publishResponse.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        var body = await client.GetStringAsync(DetailUrl(applicationId));
        body.Should().Contain("role=\"alert\"");
        body.Should().Contain("at least one category");
        body.Should().Contain("Draft version 1");
        body.Should().Contain("Publish version 1");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var version = await database.ApplicationVersions.SingleAsync(v => v.ApplicationId == applicationId);
            var application = await database.Applications.SingleAsync(a => a.Id == applicationId);

            version.Status.Should().Be(VersionStatus.Draft);
            version.PublishedUtc.Should().BeNull();
            application.CurrentVersionId.Should().BeNull();
            return true;
        });
    }

    [Fact]
    public async Task Custom_base_path_mounts_and_forms_work()
    {
        using var factory = new CustomBasePathEditorWebApplicationFactory();
        await SeedDatabaseAsync(factory);
        var client = await factory.CreateSignedInClientAsync("designer");

        var indexResponse = await client.GetAsync($"{CustomBasePathEditorWebApplicationFactory.CustomBasePath}/Applications");
        indexResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var (createToken, createHtml) = await factory.GetFormAsync(
            client,
            $"{CustomBasePathEditorWebApplicationFactory.CustomBasePath}/Applications/Create");
        var formActions = Regex.Matches(createHtml, "<form[^>]*\\baction=\"(?<action>[^\"]+)\"[^>]*>")
            .Select(match => match.Groups["action"].Value)
            .ToList();
        formActions.Should().Contain(
            $"{CustomBasePathEditorWebApplicationFactory.CustomBasePath}/Applications/Create",
            "the create screen should render a form posting to the base-path mounted action");

        var applicationTypeId = await GetApplicationTypeIdAsync(factory);
        var createResponse = await client.PostAsync(
            $"{CustomBasePathEditorWebApplicationFactory.CustomBasePath}/Applications/Create",
            BuildCreateForm("Endowment Expansion", applicationTypeId, createToken));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        createResponse.Headers.Location.Should().NotBeNull();
        createResponse.Headers.Location!.ToString().Should().StartWith(
            $"{CustomBasePathEditorWebApplicationFactory.CustomBasePath}/");

        var defaultPathResponse = await client.GetAsync("/ConfigureApp/Applications");
        defaultPathResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private const string CreateUrl = $"{BasePath}/Applications/Create";

    private static string DetailUrl(int applicationId) => $"{BasePath}/Applications/Detail/{applicationId}";

    private static int ParseDetailId(Uri location)
    {
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var match = Regex.Match(path, "/Applications/Detail/(?<id>[0-9]+)$");
        match.Success.Should().BeTrue($"the redirect should point at the detail page, but was {path}");
        return int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
    }

    private static FormUrlEncodedContent BuildCreateForm(string name, int applicationTypeId, string token)
        => BuildForm(token, ("Name", name), ("ApplicationTypeId", Invariant(applicationTypeId)));

    private static FormUrlEncodedContent BuildForm(string token, params (string Key, string Value)[] fields)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
        };

        values.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return new FormUrlEncodedContent(values);
    }

    private static string ExtractRevision(string html)
    {
        var match = Regex.Match(html, "<input[^>]*name=\"expectedRevision\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("a draft form should include its revision token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task SeedDatabaseAsync(EditorWebApplicationFactory factory)
        => await factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            await database.Database.EnsureCreatedAsync();
            return true;
        });

    private static async Task<int> GetApplicationTypeIdAsync(EditorWebApplicationFactory factory)
        => await factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var types = await designService.ListApplicationTypesAsync();
            return types.Single(type => type.Name.Contains("RFA", StringComparison.Ordinal)).Id;
        });

    private async Task<string> GetDetailTokenAsync(HttpClient client, int applicationId)
        => (await _factory.GetFormAsync(client, DetailUrl(applicationId))).Token;

    private async Task<int> GetDraftVersionIdAsync(int applicationId)
        => await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var draft = await database.ApplicationVersions
                .SingleAsync(v => v.ApplicationId == applicationId && v.Status == VersionStatus.Draft);
            return draft.Id;
        });

    private async Task<int> GetCategoryIdAsync(int versionId, string name)
        => await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var category = await database.Categories
                .SingleAsync(c => c.ApplicationVersionId == versionId && c.Name == name);
            return category.Id;
        });

    private async Task<int> GetQuestionIdAsync(int categoryId, string label)
        => await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var element = await database.Elements
                .SingleAsync(e => e.CategoryId == categoryId && e.Label == label);
            return await database.Questions
                .Where(q => q.ElementId == element.Id)
                .Select(q => q.Id)
                .SingleAsync();
        });

    private async Task<IReadOnlyList<int>> GetOptionIdsAsync(int questionId)
        => await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            return (IReadOnlyList<int>)await database.QuestionOptions
                .Where(o => o.QuestionId == questionId)
                .OrderBy(o => o.Id)
                .Select(o => o.Id)
                .ToListAsync();
        });

    private async Task<(int Categories, int Questions, int Options, int Rules)> GetVersionContentCountsAsync(int versionId)
        => await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var categoryIds = await database.Categories
                .Where(c => c.ApplicationVersionId == versionId)
                .Select(c => c.Id)
                .ToListAsync();
            var elementIds = await database.Elements
                .Where(e => categoryIds.Contains(e.CategoryId))
                .Select(e => e.Id)
                .ToListAsync();
            var questionIds = await database.Questions
                .Where(q => elementIds.Contains(q.ElementId))
                .Select(q => q.Id)
                .ToListAsync();

            var categories = categoryIds.Count;
            var questions = questionIds.Count;
            var options = await database.QuestionOptions.CountAsync(o => questionIds.Contains(o.QuestionId));
            var rules = await database.ScoringRules.CountAsync(r => questionIds.Contains(r.QuestionId));
            return (categories, questions, options, rules);
        });

    private sealed class CustomBasePathEditorWebApplicationFactory : EditorWebApplicationFactory
    {
        public const string CustomBasePath = "/admin/qab";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("QuestionAppBuilderEditor:BasePath", CustomBasePath);
            base.ConfigureWebHost(builder);
        }
    }
}
