using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class ResponseHttpTests
{
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
