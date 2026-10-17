using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task Hidden_answer_restores_before_submit_but_is_absent_after_submit()
    {
        var runtime = new TestHostFixture.FakeRuntime { Graph = TestHostFixture.FakeRuntime.BuildGraph(conditional: true) };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var responsePath = $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}";

        var initial = await CsrfAsync(host.Client, responsePath);
        await initial.PostAsync(host.Client, responsePath + "/Save", new Dictionary<string, string> { ["Answers[2].Text"] = "keep while hidden" });
        var hiddenPage = await host.Client.GetStringAsync(Https(responsePath + "?category=1"));
        hiddenPage.Should().NotContain("Question 2");

        var firstCategory = await CsrfAsync(host.Client, responsePath);
        await firstCategory.PostAsync(host.Client, responsePath + "/Navigate", new Dictionary<string, string>
        { ["CurrentCategoryIndex"] = "0", ["TargetCategoryIndex"] = "1", ["Answers[1].Text"] = "show second question", ["Answers[2].Text"] = "keep while hidden" });
        var restored = await host.Client.GetStringAsync(Https(responsePath + "?category=1"));
        restored.Should().Contain("Question 2").And.Contain("keep while hidden");

        var secondCategory = await CsrfAsync(host.Client, responsePath + "?category=1");
        await secondCategory.PostAsync(host.Client, responsePath + "/Navigate", new Dictionary<string, string>
        { ["CurrentCategoryIndex"] = "1", ["TargetCategoryIndex"] = "0", ["Answers[2].Text"] = "keep while hidden" });
        var hideAgain = await CsrfAsync(host.Client, responsePath);
        await hideAgain.PostAsync(host.Client, responsePath + "/Navigate", new Dictionary<string, string>
        { ["CurrentCategoryIndex"] = "0", ["TargetCategoryIndex"] = "1", ["Answers[1].Text"] = "", ["Answers[2].Text"] = "keep while hidden" });

        var review = await CsrfAsync(host.Client, responsePath + "/Review");
        await review.PostAsync(host.Client, responsePath + "/Submit", new Dictionary<string, string> { ["RequestId"] = Guid.NewGuid().ToString("D"), ["Answers[2].Text"] = "keep while hidden" });
        runtime.SubmittedAnswers.Should().NotContainKey(2);
    }

    [Fact]
    public async Task Category_tabs_are_keyboard_reachable_links_with_current_step()
    {
        await using var host = await TestHostFixture.StartAsync();
        await StartAsync(host);
        var html = await host.Client.GetStringAsync(Https($"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}"));
        html.Should().Contain("<nav class=\"qab-tabs\"");
        html.Should().Contain("aria-current=\"step\"");
        Regex.Matches(html, "<a class=\\\"qab-tab").Count.Should().BeGreaterThan(1);
    }

    private static async Task StartAsync(TestHostFixture host)
    {
        var csrf = await CsrfAsync(host.Client, "/App/1");
        (await csrf.PostAsync(host.Client, "/App/1/Start", new Dictionary<string, string>())).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
    private static async Task<Csrf> CsrfAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(Https(path));
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\" type=\\\"hidden\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var revision = Regex.Match(html, "name=\\\"ExpectedRevision\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        return new Csrf(token, cookie, revision);
    }
    private static Uri Https(string path) => new($"https://localhost{path}");
    private sealed record Csrf(string Token, string Cookie, string Revision)
    {
        public async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = Token;
            if (!string.IsNullOrEmpty(Revision)) values.TryAdd("ExpectedRevision", Revision);
            using var request = new HttpRequestMessage(HttpMethod.Post, Https(path)) { Content = new FormUrlEncodedContent(values) };
            request.Headers.Add("Cookie", Cookie);
            return await client.SendAsync(request);
        }
    }
}
