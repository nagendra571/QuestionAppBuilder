using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class ScoringPreviewTests
{
    [Fact]
    public async Task Preview_maps_actual_capped_and_combined_awards_with_exact_decimal_text()
    {
        using var factory = new EditorWebApplicationFactory();
        var seed = await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var application = await design.CreateApplicationAsync("Scoring diagnostics", type.Id);
            var category = await design.AddCategoryAsync(application.Id, "Capacity");
            var question = await design.AddQuestionAsync(category.Id, "Participants", AnswerType.Number, false);
            var drafts = services.GetRequiredService<IDraftService>();
            var snapshot = await drafts.GetAsync(application.Id);
            await design.AddRuleAsync(question.Id, RuleFamily.NumericRange, 0, minValue: 0, expectedRevision: snapshot.Revision);
            snapshot = await drafts.GetAsync(application.Id);
            var element = snapshot.Definition.Categories.Single().Elements.Single();
            var number = element.Question!;
            number.WholeNumbersOnly = true;
            number.MinValue = 0;
            var perUnit = number.Rules.Single();
            perUnit.AwardMethod = AwardMethod.PointsPerUnit;
            perUnit.PointsPerUnit = 2;
            perUnit.MaximumPoints = 5;
            var threshold = new CompositionRule
            {
                Name = "Precise threshold", CategoryKey = category.LogicalKey, Kind = RulePurpose.Score, Points = 9,
                Checks = [new()
                {
                    Left = new() { Kind = SourceKind.Question, Key = element.LogicalKey },
                    Comparison = Comparison.GreaterThanOrEqual,
                    Right = new() { Value = 9007199254740993.123456789012m }
                }]
            };
            CompositionRule Band(string name, string band, int points) => new()
            {
                Name = name, CategoryKey = category.LogicalKey, Kind = RulePurpose.Score,
                BandGroup = band, Points = points,
                Checks = [new() { Left = new() { Value = 1 }, Right = new() { Value = 1 }, Comparison = Comparison.Equal }]
            };
            var winner = Band("Priority award", " capacity ", 3);
            var suppressed = Band("Later award", "CAPACITY", 10);
            snapshot.Definition.Version.CompositionJson = new CompositionDefinition
                { Rules = [threshold, winner, suppressed] }.ToJson();
            await drafts.SaveAsync(new SaveDraftRequest(application.Id, snapshot.Revision, snapshot.Definition));
            var saved = await drafts.GetAsync(application.Id);
            return (ApplicationId: application.Id, QuestionId: question.Id, RuleId: perUnit.Id,
                Revision: saved.Revision, WinnerId: winner.Id);
        });
        using var client = await factory.CreateSignedInClientAsync("designer");
        var (token, _) = await factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{seed.ApplicationId}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ConfigureApp/Preview/{seed.ApplicationId}/Evaluate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                ExpectedRevision = Convert.ToBase64String(seed.Revision),
                Answers = new Dictionary<int, object> { [seed.QuestionId] = new { Number = "9007199254740993" } }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("score").GetProperty("total").GetInt32().Should().Be(8);
        root.GetProperty("score").TryGetProperty("diagnostics", out _).Should().BeFalse();
        var scoring = root.GetProperty("scoring");
        scoring.GetProperty("categories")[0].GetProperty("name").GetString().Should().Be("Capacity");
        scoring.GetProperty("categories")[0].GetProperty("points").GetInt32().Should().Be(8);
        var questionPreview = scoring.GetProperty("questions")[0];
        questionPreview.GetProperty("name").GetString().Should().Be("Participants");
        questionPreview.GetProperty("answerNumberText").GetString().Should().Be("9007199254740993");
        questionPreview.GetProperty("points").GetInt32().Should().Be(5);
        var rule = questionPreview.GetProperty("rules")[0];
        rule.GetProperty("unitsText").GetString().Should().Be("9007199254740993");
        rule.GetProperty("capReached").GetBoolean().Should().BeTrue();
        rule.GetProperty("matchedPointsText").GetString().Should().Be("5");
        rule.GetProperty("editUrl").GetString().Should().EndWith($"#rule-{seed.RuleId}");
        var combined = scoring.GetProperty("combinedRules");
        combined[0].GetProperty("outcomeCode").GetString().Should().Be("checks-not-matched");
        var check = combined[0].GetProperty("checks")[0];
        check.GetProperty("leftValueText").GetString().Should().Be("9007199254740993");
        check.GetProperty("rightValueText").GetString().Should().Be("9007199254740993.123456789012");
        check.GetProperty("matches").GetBoolean().Should().BeFalse();
        combined[1].GetProperty("configurationUrl").GetString().Should().EndWith($"#Rules/{seed.WinnerId:D}");
        combined[2].GetProperty("outcomeCode").GetString().Should().Be("band-suppressed");
        combined[2].GetProperty("supersedingRuleName").GetString().Should().Be("Priority award");
        combined[2].GetProperty("awardedPoints").GetInt32().Should().Be(0);
    }
}
