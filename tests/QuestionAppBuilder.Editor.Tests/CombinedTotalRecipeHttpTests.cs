using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class CombinedTotalRecipeHttpTests
{
    [Fact]
    public async Task Composition_save_persists_the_calculation_and_validation_together_and_evaluates_boundary_and_live_mode()
    {
        using var factory = new EditorWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = await factory.CreateSignedInClientAsync("designer");
        var baselineDefinition = new CompositionDefinition
        {
            Groups = [new QuestionGroup
            {
                Id = Guid.NewGuid(), Name = "Existing budget table", CategoryKey = seed.CategoryKey,
                Columns = ["Description"], Rows = [new GroupRow { QuestionKey = seed.ElementKeys[0], Cells = ["Current line"] }]
            }]
        };
        var baselineSave = await SaveCompositionAsync(factory, client, seed.ApplicationId, seed.Revision, baselineDefinition);
        baselineSave.StatusCode.Should().Be(HttpStatusCode.OK, baselineSave.Body);
        var baseline = await ReadDraftAsync(factory, seed.ApplicationId);
        var definition = LimitTotal(seed, existingGroups: CompositionDefinition.Read(baseline.Definition.Version.CompositionJson).Groups);

        var saved = await SaveCompositionAsync(factory, client, seed.ApplicationId, baseline.Revision, definition);

        saved.StatusCode.Should().Be(HttpStatusCode.OK, saved.Body);
        var current = await ReadDraftAsync(factory, seed.ApplicationId);
        var persisted = CompositionDefinition.Read(current.Definition.Version.CompositionJson);
        persisted.Groups.Should().ContainSingle(group => group.Name == "Existing budget table");
        persisted.Calculations.Should().ContainSingle(calculation => calculation.Id == definition.Calculations.Single().Id);
        persisted.Rules.Should().ContainSingle(rule => rule.Id == definition.Rules.Single().Id);
        var savedRevision = current.Revision;

        using var boundary = await EvaluateAsync(factory, client, seed, savedRevision, first: "2", second: "14");
        boundary.RootElement.GetProperty("answerIssues").GetArrayLength().Should().Be(0, "a sum equal to 16 satisfies the inclusive limit");
        var boundaryValue = boundary.RootElement.GetProperty("questionnaire").GetProperty("categories")[0]
            .GetProperty("calculatedValues")[0];
        boundaryValue.GetProperty("name").GetString().Should().Be("Combined budget total");
        boundaryValue.GetProperty("value").GetDecimal().Should().Be(16);

        using var missingSource = await EvaluateAsync(factory, client, seed, savedRevision, first: "", second: "15");
        missingSource.RootElement.GetProperty("answerIssues").GetArrayLength().Should().Be(0);
        missingSource.RootElement.GetProperty("questionnaire").GetProperty("categories")[0]
            .GetProperty("calculatedValues")[0].GetProperty("value").GetDecimal().Should().Be(15,
                "a missing numeric source contributes zero to the total");

        using var overLimit = await EvaluateAsync(factory, client, seed, savedRevision, first: "2", second: "15");
        overLimit.RootElement.GetProperty("questionnaire").GetProperty("categories")[0]
            .GetProperty("calculatedValues")[0].GetProperty("value").GetDecimal().Should().Be(17);
        overLimit.RootElement.GetProperty("answerIssues").EnumerateArray()
            .Should().Contain(issue => issue.GetProperty("code").GetString() == "combined-validation");
        var failed = overLimit.RootElement.GetProperty("failedValidationRules");
        failed.GetArrayLength().Should().Be(1);
        failed[0].GetProperty("showWhileAnswering").GetBoolean().Should().BeFalse();
        (await ReadLiveErrorsAsync(factory, seed.ApplicationId, first: 2, second: 15))
            .Should().BeEmpty("ShowWhileAnswering=false does not create live feedback errors");

        var liveDefinition = LimitTotal(seed, showWhileAnswering: true, existingGroups: persisted.Groups);
        var liveSave = await SaveCompositionAsync(factory, client, seed.ApplicationId, savedRevision, liveDefinition);
        liveSave.StatusCode.Should().Be(HttpStatusCode.OK, liveSave.Body);
        var liveSnapshot = await ReadDraftAsync(factory, seed.ApplicationId);
        using var liveOverLimit = await EvaluateAsync(factory, client, seed, liveSnapshot.Revision, first: "2", second: "15");
        liveOverLimit.RootElement.GetProperty("failedValidationRules")[0].GetProperty("showWhileAnswering").GetBoolean().Should().BeTrue();
        var liveErrors = await ReadLiveErrorsAsync(factory, seed.ApplicationId, first: 2, second: 15);
        liveErrors.Should().ContainSingle().Which.Message.Should().Be("The combined budget must not exceed 16.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_category_source_is_rejected_without_persisting_either_recipe_object(bool tiedSortOrder)
    {
        using var factory = new EditorWebApplicationFactory();
        var seed = await SeedAsync(factory, includeLaterCategory: true);
        if (tiedSortOrder)
        {
            await factory.WithScopeAsync(async services =>
            {
                var db = services.GetRequiredService<QuestionAppBuilderDbContext>();
                var versionIds = await db.ApplicationVersions.Where(version => version.ApplicationId == seed.ApplicationId)
                    .Select(version => version.Id).ToArrayAsync();
                var categories = await db.Categories.Where(category => versionIds.Contains(category.ApplicationVersionId))
                    .OrderBy(category => category.Id).ToArrayAsync();
                categories.Should().HaveCount(2);
                categories[1].SortOrder = categories[0].SortOrder;
                await db.SaveChangesAsync();
                return true;
            });
        }
        using var client = await factory.CreateSignedInClientAsync("designer");
        var before = await ReadDraftAsync(factory, seed.ApplicationId);
        before.Definition.Version.CompositionJson.Should().BeNull();

        var invalid = LimitTotal(seed, laterQuestionKey: seed.LaterElementKey);
        var response = await SaveCompositionAsync(factory, client, seed.ApplicationId, before.Revision, invalid);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, response.Body);
        response.Body.Should().Contain("later category");
        var after = await ReadDraftAsync(factory, seed.ApplicationId);
        after.Revision.Should().Equal(before.Revision);
        after.Definition.Version.CompositionJson.Should().BeNull("the staged calculation and rule must be rejected as one save");
        (await factory.WithScopeAsync(async services => await services.GetRequiredService<QuestionAppBuilderDbContext>()
            .ApplicationVersions.CountAsync(version => version.ApplicationId == seed.ApplicationId))).Should().Be(1);
    }

    [Fact]
    public async Task Hidden_target_skips_the_combined_validation_while_still_calculating_the_total()
    {
        using var factory = new EditorWebApplicationFactory();
        var seed = await SeedAsync(factory);
        var revised = await factory.WithScopeAsync(async services =>
        {
            var design = services.GetRequiredService<IApplicationDesignService>();
            await design.AddVisibilityPredicateAsync(seed.QuestionElementIds[2], seed.QuestionIds[0],
                ConditionOperator.Equal, ConditionJunction.All, numberValue: 99, expectedRevision: seed.Revision);
            return await services.GetRequiredService<IDraftService>().GetAsync(seed.ApplicationId);
        });
        using var client = await factory.CreateSignedInClientAsync("designer");
        var save = await SaveCompositionAsync(factory, client, seed.ApplicationId, revised.Revision, LimitTotal(seed));
        save.StatusCode.Should().Be(HttpStatusCode.OK, save.Body);

        using var preview = await EvaluateAsync(factory, client, seed, (await ReadDraftAsync(factory, seed.ApplicationId)).Revision,
            first: "2", second: "15");
        preview.RootElement.GetProperty("answerIssues").GetArrayLength().Should().Be(0);
        preview.RootElement.GetProperty("failedValidationRules").GetArrayLength().Should().Be(0,
            "a combined validation is skipped when its target question is hidden");
        preview.RootElement.GetProperty("questionnaire").GetProperty("categories")[0]
            .GetProperty("calculatedValues")[0].GetProperty("value").GetDecimal().Should().Be(17);
    }

    [Fact]
    public async Task Stale_revision_rejects_the_whole_pair_without_half_persisting()
    {
        using var factory = new EditorWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = await factory.CreateSignedInClientAsync("designer");
        var staleRevision = seed.Revision;
        var updated = await factory.WithScopeAsync(async services =>
        {
            var drafts = services.GetRequiredService<IDraftService>();
            var snapshot = await drafts.GetAsync(seed.ApplicationId);
            snapshot.Definition.Version.DisplayName = "Changed while the recipe was open";
            return await drafts.SaveAsync(new SaveDraftRequest(seed.ApplicationId, snapshot.Revision, snapshot.Definition));
        });

        var response = await SaveCompositionAsync(factory, client, seed.ApplicationId, staleRevision, LimitTotal(seed));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, response.Body);
        var after = await ReadDraftAsync(factory, seed.ApplicationId);
        after.Revision.Should().Equal(updated.Revision);
        after.Definition.Version.DisplayName.Should().Be("Changed while the recipe was open");
        var persisted = CompositionDefinition.Read(after.Definition.Version.CompositionJson);
        persisted.Calculations.Should().BeEmpty();
        persisted.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_recipe_in_open_draft_does_not_change_published_definition()
    {
        using var factory = new EditorWebApplicationFactory();
        var seed = await SeedAsync(factory);
        var publishedComposition = await factory.WithScopeAsync(async services =>
        {
            var drafts = services.GetRequiredService<IDraftService>();
            var review = await drafts.ReviewAsync(seed.ApplicationId);
            review.Errors.Should().BeEmpty();
            await drafts.PublishAsync(new PublishRequest(seed.ApplicationId, review.Revision,
                review.Warnings.Select(warning => warning.Code).ToHashSet(StringComparer.Ordinal)));
            var design = services.GetRequiredService<IApplicationDesignService>();
            var published = await design.GetLatestPublishedGraphAsync(seed.ApplicationId);
            await design.OpenDraftAsync(seed.ApplicationId);
            return published.Version.CompositionJson;
        });

        using var client = await factory.CreateSignedInClientAsync("designer");
        var draft = await ReadDraftAsync(factory, seed.ApplicationId);
        var response = await SaveCompositionAsync(factory, client, seed.ApplicationId, draft.Revision, LimitTotal(seed));
        response.StatusCode.Should().Be(HttpStatusCode.OK, response.Body);

        var (latestPublished, currentDraft) = await factory.WithScopeAsync(async services =>
        {
            var design = services.GetRequiredService<IApplicationDesignService>();
            var drafts = services.GetRequiredService<IDraftService>();
            return (await design.GetLatestPublishedGraphAsync(seed.ApplicationId), await drafts.GetAsync(seed.ApplicationId));
        });
        latestPublished.Version.Status.Should().Be(VersionStatus.Published);
        latestPublished.Version.CompositionJson.Should().Be(publishedComposition);
        currentDraft.Definition.Version.Id.Should().NotBe(latestPublished.Version.Id);
        CompositionDefinition.Read(currentDraft.Definition.Version.CompositionJson).Calculations.Should().ContainSingle();
        CompositionDefinition.Read(currentDraft.Definition.Version.CompositionJson).Rules.Should().ContainSingle();
    }

    private static async Task<ScenarioSeed> SeedAsync(EditorWebApplicationFactory factory, bool includeLaterCategory = false)
        => await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var application = await design.CreateApplicationAsync("Limit combined total " + Guid.NewGuid().ToString("N"), type.Id);
            var category = await design.AddCategoryAsync(application.Id, "Budget inputs");
            await design.AddQuestionAsync(category.Id, "Amount A", AnswerType.Number, isRequired: false);
            await design.AddQuestionAsync(category.Id, "Amount B", AnswerType.Number, isRequired: false);
            await design.AddQuestionAsync(category.Id, "Eligibility decision", AnswerType.Textbox, isRequired: false);
            Guid? laterElementKey = null;
            if (includeLaterCategory)
            {
                var later = await design.AddCategoryAsync(application.Id, "Later section");
                await design.AddQuestionAsync(later.Id, "Later value", AnswerType.Number, isRequired: false);
            }
            var snapshot = await services.GetRequiredService<IDraftService>().GetAsync(application.Id);
            var earlyElements = snapshot.Definition.Categories.Single(current => current.Id == category.Id).Elements;
            if (includeLaterCategory)
                laterElementKey = snapshot.Definition.Categories.Single(current => current.Name == "Later section").Elements.Single().LogicalKey;
            return new ScenarioSeed(application.Id, category.LogicalKey, snapshot.Revision,
                earlyElements.Select(element => element.Question!.Id).ToArray(),
                earlyElements.Select(element => element.LogicalKey).ToArray(),
                earlyElements.Select(element => element.Id).ToArray(), laterElementKey);
        });

    private static CompositionDefinition LimitTotal(ScenarioSeed seed, bool showWhileAnswering = false, Guid? laterQuestionKey = null,
        IReadOnlyList<QuestionGroup>? existingGroups = null)
    {
        var total = new CalculatedField
        {
            Id = Guid.NewGuid(), Name = "Combined budget total", CategoryKey = seed.CategoryKey,
            Operation = CalculationOperation.Sum,
            Sources =
            [
                new ValueSource { Kind = SourceKind.Question, Key = seed.ElementKeys[0] },
                new ValueSource { Kind = SourceKind.Question, Key = laterQuestionKey ?? seed.ElementKeys[1] }
            ]
        };
        var limit = new CompositionRule
        {
            Id = Guid.NewGuid(), Name = "Limit combined total", CategoryKey = seed.CategoryKey,
            TargetQuestionKey = seed.ElementKeys[2], Kind = RulePurpose.Validation,
            Message = "The combined budget must not exceed 16.", ShowWhileAnswering = showWhileAnswering,
            Checks = [new ValueCheck
            {
                Left = new ValueSource { Kind = SourceKind.Calculation, Key = total.Id },
                Comparison = Comparison.LessThanOrEqual,
                Right = new ValueSource { Kind = SourceKind.Constant, Value = 16 }
            }]
        };
        return new CompositionDefinition
        {
            Groups = existingGroups?.ToList() ?? [],
            Calculations = [total], Rules = [limit]
        };
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SaveCompositionAsync(
        EditorWebApplicationFactory factory, HttpClient client, int applicationId, byte[] expectedRevision,
        CompositionDefinition definition)
    {
        var (token, _) = await factory.GetFormAsync(client, $"/ConfigureApp/Composition/Index/{applicationId}");
        using var response = await client.PostAsync($"/ConfigureApp/Composition/Save/{applicationId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["expectedRevision"] = Convert.ToBase64String(expectedRevision),
            ["definitionJson"] = definition.ToJson()
        }));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> EvaluateAsync(EditorWebApplicationFactory factory, HttpClient client,
        ScenarioSeed seed, byte[] revision, string first, string second)
    {
        var (token, _) = await factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{seed.ApplicationId}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ConfigureApp/Preview/{seed.ApplicationId}/Evaluate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                ExpectedRevision = Convert.ToBase64String(revision),
                Answers = new Dictionary<int, object>
                {
                    [seed.QuestionIds[0]] = new { Number = first },
                    [seed.QuestionIds[1]] = new { Number = second }
                }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static Task<DraftSnapshot> ReadDraftAsync(EditorWebApplicationFactory factory, int applicationId)
        => factory.WithScopeAsync(services => services.GetRequiredService<IDraftService>().GetAsync(applicationId));

    private static async Task<IReadOnlyList<CompositionLiveAnswerError>> ReadLiveErrorsAsync(
        EditorWebApplicationFactory factory, int applicationId, int first, int second)
        => await factory.WithScopeAsync(async services =>
        {
            var snapshot = await services.GetRequiredService<IDraftService>().GetAsync(applicationId);
            var answers = new Dictionary<int, AnswerValue>
            {
                [snapshot.Definition.Categories.SelectMany(category => category.Elements).Single(element => element.Label == "Amount A").Question!.Id] = new(null, first, null),
                [snapshot.Definition.Categories.SelectMany(category => category.Elements).Single(element => element.Label == "Amount B").Question!.Id] = new(null, second, null)
            };
            var result = services.GetRequiredService<IDefinitionEvaluator>().Evaluate(snapshot.Definition, answers, requireComplete: false);
            return result.Composition!.LiveAnswerErrors;
        });

    private sealed record ScenarioSeed(int ApplicationId, Guid CategoryKey, byte[] Revision,
        int[] QuestionIds, Guid[] ElementKeys, int[] QuestionElementIds, Guid? LaterElementKey);
}
