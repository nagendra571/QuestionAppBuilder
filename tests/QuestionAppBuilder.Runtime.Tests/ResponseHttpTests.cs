using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class ResponseHttpTests
{
    [Theory]
    [InlineData("Please enter your organization name.", "Please enter your organization name.")]
    [InlineData(null, "A response is required.")]
    [InlineData("   ", "A response is required.")]
    public async Task Custom_required_message_is_used_on_navigation_direct_jump_and_submission(string? configured, string expected)
    {
        var runtime = new TestHostFixture.FakeRuntime();
        runtime.Graph.Categories.First().Elements.First(e => e.Question is not null).Question!.RequiredMessage = configured;
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var path = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}";
        var csrf = await CsrfAsync(host.Client, path);
        var blocked = await csrf.PostAsync(host.Client, path + "/Navigate",
            new Dictionary<string, string> { ["currentCategoryIndex"] = "0", ["targetCategoryIndex"] = "1" });
        (await blocked.Content.ReadAsStringAsync()).Should().Contain(expected);
        var jump = await GetAsync(host.Client, path + "?category=4");
        (await jump.Content.ReadAsStringAsync()).Should().Contain(expected);
        csrf = await CsrfAsync(host.Client, path + "/Review");
        var submit = await csrf.PostAsync(host.Client, path + "/Submit",
            new Dictionary<string, string> { ["RequestId"] = Guid.NewGuid().ToString("D") });
        (await submit.Content.ReadAsStringAsync()).Should().Contain(expected);
        host.Runtime.SubmissionCount.Should().Be(0);
    }

    [Fact]
    public async Task Five_category_journey_resumes_after_restart_and_submits()
    {
        var runtime = new TestHostFixture.FakeRuntime();
        var host = await TestHostFixture.StartAsync(runtime);
        var started = await StartAsync(host);
        started.StatusCode.Should().Be(HttpStatusCode.Redirect);
        for (var index = 0; index < 4; index++)
        {
            var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");
            var values = new Dictionary<string, string> { ["currentCategoryIndex"] = index.ToString(), ["targetCategoryIndex"] = (index + 1).ToString() };
            for (var q = 1; q <= index + 1; q++) values[$"Answers[{q}].Text"] = $"Answer {q}";
            var post = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Navigate", values);
            post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        await host.DisposeAsync();
        await using var restarted = await TestHostFixture.StartAsync(runtime);
        (await GetAsync(restarted.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}?category=4")).StatusCode.Should().Be(HttpStatusCode.OK);
        var review = await GetAsync(restarted.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitCsrf = await CsrfAsync(restarted.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        var requestId = Guid.NewGuid().ToString("D");
        var submitValues = new Dictionary<string, string> { ["RequestId"] = requestId };
        for (var q = 1; q <= 5; q++) submitValues[$"Answers[{q}].Text"] = $"Answer {q}";
        var submit = await submitCsrf.PostAsync(restarted.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit", submitValues);
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);
        restarted.Runtime.SubmissionCount.Should().Be(1);
    }

    [Fact]
    public async Task Required_error_summary_links_to_field()
    {
        await using var host = await TestHostFixture.StartAsync();
        await StartAsync(host);
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");
        var blocked = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Navigate",
            new Dictionary<string, string> { ["currentCategoryIndex"] = "0", ["targetCategoryIndex"] = "1" });
        blocked.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await blocked.Content.ReadAsStringAsync();
        html.Should().Contain("href=\"#question-1\"");
        html.Should().Contain("aria-describedby=\"error-question-1\"");
    }

    [Fact]
    public async Task Sequential_direct_jump_is_blocked()
    {
        await using var host = await TestHostFixture.StartAsync();
        await StartAsync(host);
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");
        var blocked = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Navigate",
            new Dictionary<string, string> { ["currentCategoryIndex"] = "0", ["targetCategoryIndex"] = "4" });
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Complete the required fields");
    }

    [Fact]
    public async Task Free_navigation_still_validates_submit()
    {
        var runtime = new TestHostFixture.FakeRuntime { Graph = TestHostFixture.FakeRuntime.BuildGraph(NavigationMode.Free) };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");
        var moved = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Navigate",
            new Dictionary<string, string> { ["currentCategoryIndex"] = "0", ["targetCategoryIndex"] = "4" });
        moved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var token = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        var submit = await token.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit",
            new Dictionary<string, string> { ["RequestId"] = Guid.NewGuid().ToString("D") });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await submit.Content.ReadAsStringAsync()).Should().Contain("Complete the required fields");
        host.Runtime.SubmissionCount.Should().Be(0);
    }

    [Fact]
    public async Task Closed_submissions_are_visible_and_submit_is_disabled_on_review()
    {
        var runtime = new TestHostFixture.FakeRuntime { AcceptingSubmissions = false };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);

        var review = await GetAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");

        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await review.Content.ReadAsStringAsync();
        html.Should().Contain("Submissions are currently closed");
        html.Should().Contain("disabled=\"disabled\"");
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        var submit = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit",
            new Dictionary<string, string> { ["RequestId"] = Guid.NewGuid().ToString("D") });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await submit.Content.ReadAsStringAsync()).Should().Contain("This questionnaire is not accepting submissions");
        runtime.SubmissionCount.Should().Be(0);
    }

    [Fact]
    public async Task Review_shows_option_labels_unanswered_state_and_category_edit_links()
    {
        var runtime = new TestHostFixture.FakeRuntime();
        var firstQuestion = runtime.Graph.Categories[0].Elements[0].Question!;
        firstQuestion.AnswerType = AnswerType.RadioGroup;
        firstQuestion.Options.Add(new QuestionAppBuilder.Domain.Entities.QuestionOption { Id = 51, Label = "Public services", Value = "services" });
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);

        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");
        var saved = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Navigate",
            new Dictionary<string, string>
            {
                ["currentCategoryIndex"] = "0", ["targetCategoryIndex"] = "1", ["Answers[1].SelectedOptionIds"] = "51"
            });
        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var review = await GetAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        var html = await review.Content.ReadAsStringAsync();
        review.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("Public services").And.Contain("Not answered")
            .And.Contain($"href=\"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}?category=0\"")
            .And.Contain("Edit Category 1");
        html.Should().NotContain(">51<", "the respondent should see the option label, not the stored option identifier");
    }

    [Fact]
    public async Task Save_failure_keeps_input_and_prevents_exit()
    {
        var runtime = new TestHostFixture.FakeRuntime { FailNextSave = true };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");
        var response = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Navigate", new Dictionary<string, string>
        { ["currentCategoryIndex"] = "0", ["targetCategoryIndex"] = "1", ["Answers[1].Text"] = "typed but unsaved" });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("typed but unsaved");
    }

    [Fact]
    public async Task Stale_revision_conflict_keeps_rejected_revision_and_offers_explicit_reload()
    {
        var runtime = new TestHostFixture.FakeRuntime();
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var responsePath = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}";
        var staleForm = await CsrfAsync(host.Client, responsePath);

        await runtime.SaveAsync(new QuestionAppBuilder.Application.Responses.SaveResponseRequest(
            TestHostFixture.FakeRuntime.ResponseId, Convert.FromBase64String(staleForm.Revision),
            new Dictionary<int, AnswerValue>
            {
                [1] = new("winner's saved answer", null, null)
            }));

        var stalePost = await staleForm.PostAsync(host.Client, $"{responsePath}/Save",
            new Dictionary<string, string> { ["Answers[1].Text"] = "stale typed answer" });
        stalePost.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await stalePost.Content.ReadAsStringAsync();
        body.Should().Contain("stale typed answer").And.Contain("Reload latest saved answers");
        Regex.Match(body, "name=\\\"ExpectedRevision\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value
            .Should().Be(staleForm.Revision);
        Regex.Match(body, "href=\\\"([^\\\"]+)\\\">Reload latest saved answers").Groups[1].Value
            .Should().Contain($"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}");

        var repeatedStalePost = await staleForm.PostAsync(host.Client, $"{responsePath}/Save",
            new Dictionary<string, string> { ["Answers[1].Text"] = "stale typed answer" });
        repeatedStalePost.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await runtime.GetAsync(TestHostFixture.FakeRuntime.ResponseId)).Answers[1].Text.Should().Be("winner's saved answer");
    }

    [Fact]
    public async Task Autosave_returns_authoritative_same_category_branch_markup()
    {
        var graph = TestHostFixture.FakeRuntime.BuildGraph();
        var condition = new QuestionAppBuilder.Domain.Entities.VisibilityCondition
        { Id = 99, ApplicationVersionId = graph.Version.Id, Junction = QuestionAppBuilder.Domain.Enums.ConditionJunction.All };
        condition.Predicates.Add(new QuestionAppBuilder.Domain.Entities.VisibilityPredicate
        { Id = 99, SourceQuestionId = 1, Operator = QuestionAppBuilder.Domain.Enums.ConditionOperator.Answered });
        graph.Version.VisibilityConditions.Add(condition);
        graph.Categories[0].Elements.Add(new QuestionAppBuilder.Domain.Entities.Element
        {
            Id = 99, Kind = QuestionAppBuilder.Domain.Enums.ElementKind.Question, Label = "Follow-up", SortOrder = 2,
            LogicalKey = Guid.NewGuid(), VisibilityConditionId = condition.Id, VisibilityCondition = condition,
            Question = new QuestionAppBuilder.Domain.Entities.Question { Id = 99, AnswerType = QuestionAppBuilder.Domain.Enums.AnswerType.Textbox }
        });
        var runtime = new TestHostFixture.FakeRuntime { Graph = graph };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var responsePath = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}";
        var initial = await GetAsync(host.Client, responsePath);
        (await initial.Content.ReadAsStringAsync()).Should().NotContain("name=\"Answers[99].Text\"");
        var csrf = await CsrfAsync(host.Client, responsePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, Https($"{responsePath}/Save"))
        {
            Content = csrf.Content(new Dictionary<string, string> { ["Answers[1].Text"] = "show branch" })
        };
        request.Headers.Add("Cookie", csrf.Cookie);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-Requested-With", "QuestionAppBuilder");
        var saved = await host.Client.SendAsync(request);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await saved.Content.ReadAsStringAsync();
        json.Should().Contain("liveContentHtml").And.Contain("Answers[99].Text");
    }

    [Fact]
    public async Task Retry_receipt_does_not_duplicate()
    {
        var runtime = new TestHostFixture.FakeRuntime();
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var values = new Dictionary<string, string>();
        for (var q = 1; q <= 5; q++) values[$"Answers[{q}].Text"] = $"Answer {q}";
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        var requestId = Guid.NewGuid().ToString("D"); values["RequestId"] = requestId;
        var first = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit", values);
        var second = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit", values);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect); second.StatusCode.Should().Be(HttpStatusCode.Redirect);
        runtime.SubmissionCount.Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_only_HTML_and_JSON_contain_no_score_fields()
    {
        var runtime = new TestHostFixture.FakeRuntime { Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: ResultVisibility.ConfirmationOnly) };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var answers = new Dictionary<string, string>();
        for (var q = 1; q <= 5; q++) answers[$"Answers[{q}].Text"] = $"Answer {q}";
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        answers["RequestId"] = Guid.NewGuid().ToString("D");
        var submit = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit", answers);
        var receiptUrl = submit.Headers.Location!.ToString();
        var html = await host.Client.GetStringAsync(receiptUrl);
        html.Should().NotContain("data-total-score").And.NotContain("Category scores");
        var json = await host.Client.GetAsync(receiptUrl + "?format=json");
        (await json.Content.ReadAsStringAsync()).Should().NotContain("Total").And.NotContain("Categories");
    }

    [Fact]
    public async Task Legacy_receipt_keeps_result_visibility_while_reopened_view_uses_without_scores_setting()
    {
        var runtime = new TestHostFixture.FakeRuntime
        {
            Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: ResultVisibility.Total)
        };
        runtime.Graph.Version.SubmittedApplicationVisibility = SubmittedApplicationVisibility.WithoutScores;
        await using var host = await TestHostFixture.StartAsync(runtime);
        await SubmitResponseAsync(host);
        var receiptPath = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Receipt";

        var receipt = await host.Client.GetStringAsync(Https(receiptPath));
        receipt.Should().Contain("data-total-score=\"0\"").And.NotContain("qab-submitted-application")
            .And.Contain("View submitted application").And.Contain("/Submitted");
        var receiptJson = await host.Client.GetStringAsync(Https(receiptPath + "?format=json"));
        receiptJson.Should().Contain("\"total\":0").And.NotContain("submittedApplication");

        var html = await host.Client.GetStringAsync(Https($"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submitted"));

        html.Should().Contain("Answer 1").And.NotContain("name=\"Answers[")
            .And.NotContain("Send application").And.NotContain("runtime.js").And.Contain("Application introduction");
        html.Should().NotContain("data-total-score").And.NotContain("Category scores");
        html.Should().NotContain("<form", "a submitted application view is browse-only");
        runtime.SubmissionCount.Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_total_and_answer_review_shows_all_categories_as_read_only()
    {
        var runtime = new TestHostFixture.FakeRuntime
        {
            Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: ResultVisibility.ConfirmationAndAnswerReview)
        };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await SubmitResponseAsync(host);
        var receiptPath = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Receipt";

        var html = await host.Client.GetStringAsync(Https(receiptPath));

        html.Should().Contain("data-total-score=\"2\"").And.Contain("Question 1")
            .And.Contain("+1 points").And.NotContain("+0 points")
            .And.Contain("Supporter").And.Contain("Summary").And.Contain("Application introduction");
        for (var category = 1; category <= 5; category++) html.Should().Contain($"Category {category}");
        html.Should().NotContain("<form").And.NotContain("runtime.js");
        var json = await host.Client.GetStringAsync(Https(receiptPath + "?format=json"));
        json.Should().Contain("\"total\":2").And.NotContain("submittedApplication")
            .And.NotContain("pointsEarned").And.NotContain("\"categories\"");
    }

    [Fact]
    public async Task Confirmation_only_receipt_can_reopen_with_scores()
    {
        var runtime = new TestHostFixture.FakeRuntime
        {
            Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: ResultVisibility.ConfirmationOnly)
        };
        runtime.Graph.Version.SubmittedApplicationVisibility = SubmittedApplicationVisibility.WithScores;
        await using var host = await TestHostFixture.StartAsync(runtime);
        await SubmitResponseAsync(host);
        var responseId = TestHostFixture.FakeRuntime.ResponseId;

        var receipt = await host.Client.GetStringAsync(Https($"/App/Responses/{responseId}/Receipt"));
        receipt.Should().NotContain("data-total-score").And.NotContain("qab-submitted-application");
        var submitted = await host.Client.GetStringAsync(Https($"/App/Responses/{responseId}/Submitted"));
        submitted.Should().Contain("data-total-score=\"2\"").And.Contain("+1 points").And.Contain("Supporter");
        submitted.Should().NotContain("+0 points").And.NotContain("<form");
        var clamped = await host.Client.GetStringAsync(Https($"/App/Responses/{responseId}/Submitted?category=999"));
        clamped.Should().Contain("Category 5").And.Contain("Answer 5");
    }

    [Theory]
    [InlineData(ResultVisibility.Total, false)]
    [InlineData(ResultVisibility.CategoryBreakdown, true)]
    public async Task Legacy_scored_receipts_keep_their_existing_shape(ResultVisibility visibility, bool hasCategoryBreakdown)
    {
        var runtime = new TestHostFixture.FakeRuntime
        {
            Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: visibility)
        };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await SubmitResponseAsync(host);
        var receiptPath = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Receipt";

        var html = await host.Client.GetStringAsync(Https(receiptPath));
        html.Should().Contain("data-total-score=\"0\"").And.NotContain("qab-submitted-application");
        var json = await host.Client.GetStringAsync(Https(receiptPath + "?format=json"));
        json.Should().NotContain("submittedApplication").And.Contain("\"total\":0");
        if (hasCategoryBreakdown)
        {
            html.Should().Contain("Category scores");
            json.Should().Contain("\"categories\"");
        }
        else
        {
            html.Should().NotContain("Category scores");
            json.Should().NotContain("\"categories\"");
        }
    }

    private static async Task SubmitResponseAsync(TestHostFixture host)
    {
        await StartAsync(host);
        var answers = new Dictionary<string, string>();
        for (var q = 1; q <= 5; q++) answers[$"Answers[{q}].Text"] = $"Answer {q}";
        answers["RequestId"] = Guid.NewGuid().ToString("D");
        var csrf = await CsrfAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Review");
        var submit = await csrf.PostAsync(host.Client, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submit", answers);
        submit.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    private static async Task<Csrf> CsrfAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(Https(path));
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\" type=\\\"hidden\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        if (string.IsNullOrEmpty(token)) token = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var revision = Regex.Match(html, "name=\\\"ExpectedRevision\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        return new Csrf(token, cookie, revision);
    }

    private static async Task<HttpResponseMessage> StartAsync(TestHostFixture host)
    {
        var csrf = await CsrfAsync(host.Client, "/App/1");
        return await csrf.PostAsync(host.Client, "/App/1/Start", new Dictionary<string, string>());
    }
    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => client.GetAsync(Https(path));
    private static Uri Https(string path) => new($"https://localhost{path}");

    private sealed record Csrf(string Token, string Cookie, string Revision)
    {
        public FormUrlEncodedContent Content(Dictionary<string, string> values)
        { values["__RequestVerificationToken"] = Token; if (!string.IsNullOrEmpty(Revision)) values.TryAdd("ExpectedRevision", Revision); return new FormUrlEncodedContent(values); }
        public async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> values)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Https(path)) { Content = Content(values) };
            request.Headers.Add("Cookie", Cookie);
            return await client.SendAsync(request);
        }
    }
}
