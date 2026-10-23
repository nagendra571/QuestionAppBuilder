using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class DraftWorkspaceTests : IDisposable
{
    private readonly EditorWebApplicationFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Required_message_saves_duplicates_and_survives_new_version_without_changing_published_question()
    {
        var appId = await CreateDraftAsync();
        var questionId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<IDraftService>().GetAsync(appId)).Definition.Categories.First().Elements.First().Question!.Id);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, html) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{appId}");
        html.Should().Contain("Required-answer message (optional)").And.Contain("Leave blank");
        var response = await client.PostAsync($"/ConfigureApp/Drafts/{appId}/Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["ApplicationId"] = appId.ToString(),
            ["ExpectedRevision"] = ReadRevision(html), ["Version.DisplayName"] = "Custom validation",
            ["Version.SubmitLabel"] = "Submit", ["Questions[0].Id"] = questionId.ToString(),
            ["Questions[0].AnswerType"] = "Number", ["Questions[0].IsRequired"] = "true",
            ["Questions[0].RequiredMessage"] = "Please enter your annual income."
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Found, await response.Content.ReadAsStringAsync());
        await _factory.WithScopeAsync(async services =>
        {
            var drafts = services.GetRequiredService<IDraftService>();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var saved = await drafts.GetAsync(appId);
            var element = saved.Definition.Categories.First().Elements.First();
            element.Question!.RequiredMessage.Should().Be("Please enter your annual income.");
            var duplicate = await design.DuplicateElementAsync(element.Id, expectedRevision: saved.Revision);
            duplicate.Question!.RequiredMessage.Should().Be(element.Question.RequiredMessage);
            var review = await drafts.ReviewAsync(appId);
            await drafts.PublishAsync(new PublishRequest(appId, review.Revision, review.Warnings.Select(w => w.Code).ToHashSet()));
            await design.OpenDraftAsync(appId);
            var next = await drafts.GetAsync(appId);
            next.Definition.Categories.First().Elements.First().Question!.RequiredMessage.Should().Be("Please enter your annual income.");
            next.Definition.Categories.First().Elements.First().Question!.RequiredMessage = "New draft message";
            await drafts.SaveAsync(new SaveDraftRequest(appId, next.Revision, next.Definition));
            var published = await design.GetLatestPublishedGraphAsync(appId);
            published.Categories.First().Elements.First().Question!.RequiredMessage.Should().Be("Please enter your annual income.");
            return true;
        });
    }

    [Fact]
    public async Task Workspace_save_accepts_more_form_values_than_the_framework_default()
    {
        var appId = await CreateDraftAsync();
        var questionId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<IDraftService>().GetAsync(appId)).Definition.Categories.First().Elements.First().Question!.Id);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, html) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{appId}");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("ApplicationId", appId.ToString()),
            new("ExpectedRevision", ReadRevision(html)), new("Version.DisplayName", "Large workspace"),
            new("Version.SubmitLabel", "Submit"), new("Questions[0].Id", questionId.ToString()),
            new("Questions[0].AnswerType", "Number"), new("Questions[0].IsRequired", "true"),
            new("Questions[0].RequiredMessage", "Large forms still save.")
        };
        // A ~50-question workspace posts more than ASP.NET Core's default 1,024 form values.
        for (var i = 0; i < 1500; i++) fields.Add(new($"Questions[0].MaxLength", ""));

        var response = await client.PostAsync($"/ConfigureApp/Drafts/{appId}/Save", new FormUrlEncodedContent(fields));

        response.StatusCode.Should().Be(HttpStatusCode.Found, await response.Content.ReadAsStringAsync());
        await _factory.WithScopeAsync(async services =>
        {
            var saved = await services.GetRequiredService<IDraftService>().GetAsync(appId);
            saved.Definition.Categories.First().Elements.First().Question!.RequiredMessage.Should().Be("Large forms still save.");
            return true;
        });
    }

    [Fact]
    public async Task Workspace_exposes_all_six_answer_types_and_revision_bound_readiness()
    {
        var appId = await CreateDraftAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var html = await client.GetStringAsync($"/ConfigureApp/Drafts/{appId}");
        html.Should().Contain("Questionnaire settings").And.Contain("Visibility rules").And.Contain("Scoring guide").And.Contain("Readiness");
        foreach (var type in Enum.GetNames<AnswerType>()) html.Should().Contain($"value=\"{type}\"");
        var readiness = await client.GetStringAsync($"/ConfigureApp/Drafts/{appId}/Readiness");
        readiness.Should().Contain("\"revision\"").And.Contain("\"errors\"");
    }

    [Fact]
    public async Task Create_duplicate_reorder_edit_publish_all_six_types()
    {
        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            await database.Database.EnsureCreatedAsync();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var drafts = services.GetRequiredService<IDraftService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var app = await design.CreateApplicationAsync("Six types " + Guid.NewGuid().ToString("N"), type.Id);
            var first = await design.AddCategoryAsync(app.Id, "Contact");
            var second = await design.AddCategoryAsync(app.Id, "Eligibility");
            var questions = new Dictionary<AnswerType, Question>();
            foreach (var answerType in Enum.GetValues<AnswerType>())
                questions[answerType] = await design.AddQuestionAsync(first.Id, answerType + " question", answerType, true);
            foreach (var answerType in new[] { AnswerType.Dropdown, AnswerType.RadioGroup, AnswerType.Checkboxes })
            {
                var option = await design.AddOptionAsync(questions[answerType].Id, "Yes", "yes");
                await design.AddOptionAsync(questions[answerType].Id, "No", "no");
                if (answerType == AnswerType.RadioGroup)
                    await design.AddRuleAsync(questions[answerType].Id, RuleFamily.Selection, 4, MembershipOperator.AnyOf, new[] { option.Id });
            }
            await design.AddRuleAsync(questions[AnswerType.Number].Id, RuleFamily.NumericRange, 5, minValue: 1, maxValue: 10);

            var beforeBranch = await drafts.GetAsync(app.Id);
            var targetElement = beforeBranch.Definition.Categories.SelectMany(c => c.Elements).Single(e => e.Question?.Id == questions[AnswerType.Dropdown].Id);
            var branch = await design.AddVisibilityPredicateAsync(targetElement.Id, questions[AnswerType.Textbox].Id,
                ConditionOperator.Answered, ConditionJunction.All, expectedRevision: beforeBranch.Revision);
            branch.Predicates.Should().ContainSingle().Which.SourceQuestionId.Should().Be(questions[AnswerType.Textbox].Id);

            var draftBefore = await drafts.GetAsync(app.Id);
            var originalRadio = draftBefore.Definition.Categories.SelectMany(c => c.Elements).Single(e => e.Question?.Id == questions[AnswerType.RadioGroup].Id);
            var clonedElement = await design.DuplicateElementAsync(originalRadio.Id, expectedRevision: draftBefore.Revision);
            clonedElement.LogicalKey.Should().NotBe(originalRadio.LogicalKey);
            clonedElement.Question!.Options.Select(o => o.LogicalKey).Should().NotIntersectWith(originalRadio.Question!.Options.Select(o => o.LogicalKey));
            clonedElement.Question.Rules.Select(r => r.LogicalKey).Should().NotIntersectWith(originalRadio.Question.Rules.Select(r => r.LogicalKey));
            var afterElement = await drafts.GetAsync(app.Id);
            await design.MoveElementAsync(clonedElement.Id, 0, expectedRevision: afterElement.Revision);
            var afterMove = await drafts.GetAsync(app.Id);
            afterMove.Definition.Categories.SelectMany(c => c.Elements).OrderBy(e => e.SortOrder).First().Id.Should().Be(clonedElement.Id);
            await design.MoveCategoryAsync(second.Id, 0, expectedRevision: afterMove.Revision);
            var afterCategoryMove = await drafts.GetAsync(app.Id);
            afterCategoryMove.Definition.Categories.OrderBy(c => c.SortOrder).First().Id.Should().Be(second.Id);

            var review = await drafts.ReviewAsync(app.Id);
            review.Errors.Should().BeEmpty();
            await drafts.PublishAsync(new PublishRequest(app.Id, review.Revision, review.Warnings.Select(w => w.Code).ToHashSet(StringComparer.Ordinal)));
            await database.Entry(app).ReloadAsync();
            var published = await design.GetLatestPublishedGraphAsync(app.Id);
            var categoryKey = published.Categories.Single(c => c.Id == first.Id).LogicalKey;
            await design.OpenDraftAsync(app.Id);
            var cloned = await drafts.GetAsync(app.Id);
            cloned.Definition.Categories.Single(c => c.Name == "Contact").LogicalKey.Should().Be(categoryKey);
            return true;
        });
    }

    [Fact]
    public async Task Draft_conflict_preserves_local_input_and_refuses_stale_save()
    {
        var appId = await CreateDraftAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, html) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{appId}");
        var revision = ReadRevision(html);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["ApplicationId"] = appId.ToString(),
            ["ExpectedRevision"] = revision, ["Version.DisplayName"] = "Saved local draft",
            ["Version.SubmitLabel"] = "Send", ["Version.ConfirmationText"] = "Thank you",
            ["Version.NavigationMode"] = nameof(NavigationMode.Sequential),
            ["Version.RespondentAccessMode"] = nameof(RespondentAccessMode.HostSignIn),
            ["Version.ResultVisibility"] = nameof(ResultVisibility.Total),
        };
        var firstSave = await client.PostAsync($"/ConfigureApp/Drafts/{appId}/Save", new FormUrlEncodedContent(form));
        firstSave.StatusCode.Should().Be(HttpStatusCode.Found, await firstSave.Content.ReadAsStringAsync());
        var stale = await client.PostAsync($"/ConfigureApp/Drafts/{appId}/Save", new FormUrlEncodedContent(form));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("changed elsewhere");
        await _factory.WithScopeAsync(async services =>
        {
            var draft = await services.GetRequiredService<IDraftService>().GetAsync(appId);
            draft.Definition.Version.DisplayName.Should().Be("Saved local draft");
            return true;
        });
    }

    [Fact]
    public async Task Save_rejects_a_posted_entity_that_is_not_part_of_the_draft()
    {
        var appId = await CreateDraftAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, html) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{appId}");
        var revision = ReadRevision(html);
        var response = await client.PostAsync($"/ConfigureApp/Drafts/{appId}/Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["ApplicationId"] = appId.ToString(), ["ExpectedRevision"] = revision,
            ["Categories[0].Id"] = "999999", ["Categories[0].Name"] = "malicious"
        }));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        body.Should().Contain("outside this version");
    }

    [Fact]
    public void Html_encoded_base64_revision_is_decoded_before_url_form_post()
    {
        const string revision = "+w==";
        var html = $"<input name=\"ExpectedRevision\" value=\"{HtmlEncoder.Default.Encode(revision)}\">";
        var parsed = ReadRevision(html);
        parsed.Should().Be(revision);
        Convert.FromBase64String(parsed).Should().Equal(new byte[] { 0xfb });
    }

    [Fact]
    public async Task Changing_warning_ack_revision_blocks_publish()
    {
        var appId = await CreateDraftAsync();
        await _factory.WithScopeAsync(async services =>
        {
            var design = services.GetRequiredService<IApplicationDesignService>();
            var drafts = services.GetRequiredService<IDraftService>();
            var snapshot = await drafts.GetAsync(appId);
            var questionId = snapshot.Definition.Categories.SelectMany(c => c.Elements).Select(e => e.Question).First(q => q is not null)!.Id;
            await design.AddRuleAsync(questionId, RuleFamily.NumericRange, 2, minValue: 1, maxValue: 10, expectedRevision: snapshot.Revision);
            snapshot = await drafts.GetAsync(appId);
            await design.AddRuleAsync(questionId, RuleFamily.NumericRange, 3, minValue: 5, maxValue: 15, expectedRevision: snapshot.Revision);
            var review = await drafts.ReviewAsync(appId);
            review.Warnings.Select(x => x.Code).Should().Contain("overlapping-numeric-rules");
            var edit = await drafts.GetAsync(appId);
            edit.Definition.Version.DisplayName = "Changed after warning review";
            await drafts.SaveAsync(new SaveDraftRequest(appId, edit.Revision, edit.Definition));
            var publish = () => drafts.PublishAsync(new PublishRequest(appId, review.Revision,
                review.Warnings.Select(x => x.Code).ToHashSet(StringComparer.Ordinal)));
            await publish.Should().ThrowAsync<DraftConflictException>();
            return true;
        });
    }

    [Fact]
    public async Task Preview_matches_runtime_evaluator_and_creates_no_response_or_submission()
    {
        var appId = await CreateDraftAsync();
        var questionId = await _factory.WithScopeAsync(async services =>
        {
            var design = services.GetRequiredService<IApplicationDesignService>();
            var graph = await design.GetDraftGraphAsync(appId);
            var qid = graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question).First(q => q is not null)!.Id;
            await design.AddRuleAsync(qid, RuleFamily.NumericRange, 7, minValue: 1, maxValue: 10);
            await design.AddRuleAsync(qid, RuleFamily.NumericRange, 5, minValue: 5, maxValue: 15);
            return qid;
        });
        var warnings = await _factory.WithScopeAsync(async services => (await services.GetRequiredService<IDraftService>().ReviewAsync(appId)).Warnings);
        warnings.Should().ContainSingle("overlapping scoring ranges are warnings, not preview blockers");
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{appId}");
        var before = await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<QuestionAppBuilderDbContext>();
            return (await db.ResponseDrafts.CountAsync(), await db.Submissions.CountAsync());
        });
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ConfigureApp/Preview/{appId}/Evaluate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Answers = new Dictionary<int, object> { [questionId] = new { Number = "5", SelectedOptionIds = Array.Empty<int>() } } }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token);
        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, json);
        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("score").GetProperty("total").GetInt32().Should().Be(7);
        parsed.RootElement.GetProperty("questionnaire").GetProperty("preview").GetBoolean().Should().BeTrue();
        parsed.RootElement.GetProperty("html").GetString().Should().Contain("data-question-id=\"").And.Contain("Annual income");

        var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"/ConfigureApp/Preview/{appId}/Evaluate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                Answers = new Dictionary<int, object> { [questionId] = new { Number = "5", SelectedOptionIds = Array.Empty<int>() } },
                Submit = true
            }), Encoding.UTF8, "application/json")
        };
        submitRequest.Headers.Add("RequestVerificationToken", token);
        var simulatedReceipt = await client.SendAsync(submitRequest);
        var receiptJson = await simulatedReceipt.Content.ReadAsStringAsync();
        simulatedReceipt.StatusCode.Should().Be(HttpStatusCode.OK, receiptJson);
        using var receipt = JsonDocument.Parse(receiptJson);
        receipt.RootElement.GetProperty("isReceipt").GetBoolean().Should().BeTrue($"only evaluator errors block a simulated receipt; overlap warnings are readiness guidance. Response: {receiptJson}");
        receipt.RootElement.GetProperty("html").GetString().Should().Contain("This is a preview. No response or submission was created.");

        var storedGraph = await _factory.WithScopeAsync(services => services.GetRequiredService<IDraftService>().GetAsync(appId));
        var expected = new DefinitionEvaluator().Evaluate(storedGraph.Definition,
            new Dictionary<int, AnswerValue> { [questionId] = new AnswerValue(null, 5, Array.Empty<int>()) }, false);
        parsed.RootElement.GetProperty("score").GetProperty("total").GetInt32().Should().Be(expected.Score.Total);
        var after = await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<QuestionAppBuilderDbContext>();
            return (await db.ResponseDrafts.CountAsync(), await db.Submissions.CountAsync());
        });
        after.Should().Be(before);
    }

    [Fact]
    public async Task Preview_uses_sequential_required_gate_preserves_answers_and_honors_result_visibility()
    {
        var applicationId = await CreateDraftAsync();
        var questionIds = await _factory.WithScopeAsync(async services =>
        {
            var design = services.GetRequiredService<IApplicationDesignService>();
            var category = await design.AddCategoryAsync(applicationId, "Additional details");
            var secondQuestion = await design.AddQuestionAsync(category.Id, "Reason for applying", AnswerType.Textbox, isRequired: true);
            var drafts = services.GetRequiredService<IDraftService>();
            var graph = await drafts.GetAsync(applicationId);
            graph.Definition.Version.NavigationMode = NavigationMode.Sequential;
            await drafts.SaveAsync(new SaveDraftRequest(applicationId, graph.Revision, graph.Definition));
            return graph.Definition.Categories.SelectMany(item => item.Elements).Where(item => item.Question is not null)
                .Select(item => item.Question!.Id).ToArray();
        });
        questionIds.Should().HaveCount(2);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{applicationId}");
        async Task<JsonDocument> EvaluateAsync(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/ConfigureApp/Preview/{applicationId}/Evaluate")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("RequestVerificationToken", token);
            var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, json);
            return JsonDocument.Parse(json);
        }

        using (var blocked = await EvaluateAsync(new { CategoryIndex = 0, TargetCategoryIndex = 1, Answers = new Dictionary<int, object>() }))
        {
            blocked.RootElement.GetProperty("categoryIndex").GetInt32().Should().Be(0);
            blocked.RootElement.GetProperty("html").GetString().Should().Contain("Complete the required fields before continuing.");
        }

        var answers = new Dictionary<int, object>
        {
            [questionIds[0]] = new { Number = "5" },
            [questionIds[1]] = new { Text = "To serve my community" }
        };
        using (var moved = await EvaluateAsync(new { CategoryIndex = 0, TargetCategoryIndex = 1, Answers = new Dictionary<int, object> { [questionIds[0]] = answers[questionIds[0]] } }))
        {
            moved.RootElement.GetProperty("categoryIndex").GetInt32().Should().Be(1);
            moved.RootElement.GetProperty("html").GetString().Should().Contain("Reason for applying");
        }
        using (var review = await EvaluateAsync(new { CategoryIndex = 1, Review = true, Answers = answers }))
        {
            review.RootElement.GetProperty("html").GetString().Should().Contain("5").And.Contain("To serve my community");
        }

        foreach (var visibility in new[] { ResultVisibility.ConfirmationOnly, ResultVisibility.Total, ResultVisibility.CategoryBreakdown })
        {
            await _factory.WithScopeAsync(async services =>
            {
                var drafts = services.GetRequiredService<IDraftService>();
                var graph = await drafts.GetAsync(applicationId);
                graph.Definition.Version.ResultVisibility = visibility;
                await drafts.SaveAsync(new SaveDraftRequest(applicationId, graph.Revision, graph.Definition));
                return true;
            });
            using var receipt = await EvaluateAsync(new { Submit = true, Answers = answers });
            var html = receipt.RootElement.GetProperty("html").GetString()!;
            receipt.RootElement.GetProperty("isReceipt").GetBoolean().Should().BeTrue();
            html.Should().Contain("This is a preview. No response or submission was created.");
            if (visibility == ResultVisibility.ConfirmationOnly) html.Should().NotContain("data-preview-total");
            if (visibility == ResultVisibility.Total) html.Should().Contain("data-preview-total");
            if (visibility == ResultVisibility.CategoryBreakdown) html.Should().Contain("Category results").And.Contain("data-preview-total");
        }
    }

    [Fact]
    public async Task Non_designer_cannot_reach_preview_or_scoring_trace()
    {
        var appId = await CreateDraftAsync();
        var client = await _factory.CreateSignedInClientAsync("filler");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ConfigureApp/Preview/{appId}/Evaluate")
        {
            Content = new StringContent("{\"answers\":{}}", Encoding.UTF8, "application/json")
        };
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<int> CreateDraftAsync()
    {
        await _factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync();
            return true;
        });
        return await _factory.WithScopeAsync(async services =>
        {
            var design = services.GetRequiredService<IApplicationDesignService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var application = await design.CreateApplicationAsync("Workspace test " + Guid.NewGuid().ToString("N"), type.Id);
            var category = await design.AddCategoryAsync(application.Id, "Identity");
            await design.AddQuestionAsync(category.Id, "Annual income", AnswerType.Number, true);
            return application.Id;
        });
    }

    private static string ReadRevision(string html)
    {
        var match = Regex.Match(html, "name=\"ExpectedRevision\" value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the form includes a draft revision");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
