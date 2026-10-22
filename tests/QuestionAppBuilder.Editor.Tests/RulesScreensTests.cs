using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class RulesScreensTests : IDisposable
{
    private readonly EditorWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Add_presence_rule_on_textbox_renders_points()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.Textbox);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Family"] = nameof(RuleFamily.Presence),
                ["Points"] = "1",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("Scoring rule");
        body.Should().Contain("Presence");
        body.Should().Contain("+1 points");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var rule = await database.ScoringRules.SingleAsync(r => r.QuestionId == questionId);

            rule.Family.Should().Be(RuleFamily.Presence);
            rule.Points.Should().Be(1);
            rule.MembershipOperator.Should().BeNull();
            rule.MinValue.Should().BeNull();
            rule.MaxValue.Should().BeNull();
            return true;
        });
    }

    [Fact]
    public async Task Add_selection_rule_with_options_renders_them()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var (firstOptionId, secondOptionId) = await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var first = await designService.AddOptionAsync(questionId, "Previously funded", "yes-prev");
            var second = await designService.AddOptionAsync(questionId, "First application", "no-first");
            return (first.Id, second.Id);
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("applicationId", applicationId.ToString(CultureInfo.InvariantCulture)),
                new("Family", nameof(RuleFamily.Selection)),
                new("Points", "5"),
                new("MembershipOperator", nameof(MembershipOperator.AnyOf)),
                new("OptionIds", firstOptionId.ToString(CultureInfo.InvariantCulture)),
                new("OptionIds", secondOptionId.ToString(CultureInfo.InvariantCulture)),
                new("__RequestVerificationToken", token),
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("Scoring rule");
        body.Should().Contain("Any Of");
        body.Should().Contain("Previously funded");
        body.Should().Contain("First application");
        body.Should().Contain("+5 points");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var rule = await database.ScoringRules.SingleAsync(r => r.QuestionId == questionId);
            var linkedOptionIds = await database.ScoringRuleOptions
                .Where(ruleOption => ruleOption.ScoringRuleId == rule.Id)
                .Select(ruleOption => ruleOption.QuestionOptionId)
                .ToListAsync();

            rule.Family.Should().Be(RuleFamily.Selection);
            rule.Points.Should().Be(5);
            rule.MembershipOperator.Should().Be(MembershipOperator.AnyOf);
            linkedOptionIds.Should().BeEquivalentTo(new[] { firstOptionId, secondOptionId });
            return true;
        });
    }

    [Fact]
    public async Task Add_numeric_range_rule_renders_bounds()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.Number);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Family"] = nameof(RuleFamily.NumericRange),
                ["Points"] = "3",
                ["MinValue"] = "10",
                ["MaxValue"] = "25",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("Scoring rule");
        body.Should().Contain("10");
        body.Should().Contain("25");
        body.Should().Contain("+3 points");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var rule = await database.ScoringRules.SingleAsync(r => r.QuestionId == questionId);

            rule.Family.Should().Be(RuleFamily.NumericRange);
            rule.MinValue.Should().Be(10m);
            rule.MaxValue.Should().Be(25m);
            return true;
        });
    }

    [Fact]
    public async Task Add_rule_rejects_an_overflowing_per_unit_rate_before_calling_the_design_service()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.Number);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, html) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{applicationId}");
        var revisionMatch = Regex.Match(html, "name=\"expectedRevision\" value=\"([^\"]+)\"");
        revisionMatch.Success.Should().BeTrue("the draft workspace form carries its current revision");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/ConfigureApp/Structure/AddRule?applicationId={applicationId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["questionId"] = questionId.ToString(CultureInfo.InvariantCulture),
                ["family"] = nameof(RuleFamily.NumericRange),
                ["points"] = "0",
                ["awardMethod"] = "PointsPerUnit",
                ["pointsPerUnit"] = "2147483648",
                ["expectedRevision"] = WebUtility.HtmlDecode(revisionMatch.Groups[1].Value),
                ["__RequestVerificationToken"] = token,
            })
        };
        request.Headers.Add("X-Requested-With", "QuestionAppBuilder");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            (await database.ScoringRules.CountAsync(rule => rule.QuestionId == questionId)).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Selection_without_operator_is_friendly_error()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var optionId = await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var option = await designService.AddOptionAsync(questionId, "Previously funded", "yes-prev");
            return option.Id;
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Family"] = nameof(RuleFamily.Selection),
                ["Points"] = "5",
                ["OptionIds"] = optionId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("role=\"alert\"");
        body.Should().Contain("membership operator");
        body.Should().NotContain("Scoring rule");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var ruleCount = await database.ScoringRules.CountAsync(r => r.QuestionId == questionId);

            ruleCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Unknown_family_value_is_friendly_error()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.Textbox);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Family"] = "999",
                ["Points"] = "1",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("role=\"alert\"");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var ruleCount = await database.ScoringRules.CountAsync(r => r.QuestionId == questionId);

            ruleCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Selection_with_invalid_operator_value_is_friendly_error()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var optionId = await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var option = await designService.AddOptionAsync(questionId, "Previously funded", "yes-prev");
            return option.Id;
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Family"] = nameof(RuleFamily.Selection),
                ["Points"] = "5",
                ["MembershipOperator"] = "999",
                ["OptionIds"] = optionId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("role=\"alert\"");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var ruleCount = await database.ScoringRules.CountAsync(r => r.QuestionId == questionId);

            ruleCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Numeric_range_with_min_above_max_is_friendly_error()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.Number);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddRuleUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Family"] = nameof(RuleFamily.NumericRange),
                ["Points"] = "3",
                ["MinValue"] = "30",
                ["MaxValue"] = "10",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("role=\"alert\"");
        body.Should().Contain("less than or equal");
        body.Should().NotContain("Scoring rule");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var ruleCount = await database.ScoringRules.CountAsync(r => r.QuestionId == questionId);

            ruleCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Remove_rule_deletes_row()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var ruleId = await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var rule = await designService.AddRuleAsync(questionId, RuleFamily.Presence, 1);
            return rule.Id;
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var before = await client.GetStringAsync(DetailUrl(applicationId));
        before.Should().Contain("Scoring rule");

        var response = await client.PostAsync(
            RemoveRuleUrl(ruleId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().NotContain("Scoring rule");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var ruleCount = await database.ScoringRules.CountAsync(r => r.Id == ruleId);

            ruleCount.Should().Be(0);
            return true;
        });
    }

    private static string DetailUrl(int applicationId)
        => $"/ConfigureApp/Applications/Detail/{applicationId}";

    private static string AddRuleUrl(int questionId)
        => $"/ConfigureApp/Rules/Add/{questionId}";

    private static string RemoveRuleUrl(int ruleId)
        => $"/ConfigureApp/Rules/Remove/{ruleId}";

    private async Task<(int ApplicationId, int CategoryId, int QuestionId, int ElementId)> CreateQuestionAsync(AnswerType answerType)
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = await SeedAndGetDesignServiceAsync(services);
            var application = await designService.CreateApplicationAsync("Endowment Expansion", await GetRfaTypeIdAsync(designService));
            var category = await designService.AddCategoryAsync(application.Id, "Site Information");
            var question = await designService.AddQuestionAsync(category.Id, "Licensed infant capacity", answerType);
            return (application.Id, category.Id, question.Id, question.ElementId);
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
