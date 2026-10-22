using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class LiveCompositionValidationTests
{
    private const string LiveMessage = "The total must not exceed $1,000.";
    private const string DeferredMessage = "The separate submission check also failed.";
    private const string ResponsePath = "/App/Responses/" + "9b91b90c-0cb7-40b4-b185-57d99bcb31a2";

    [Fact]
    public async Task Enabled_live_rule_renders_on_get_and_autosave_but_clears_when_answers_pass()
    {
        var runtime = new TestHostFixture.FakeRuntime { Graph = BuildGraph(showLive: true, includeDeferredRule: true) };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);
        var response = await runtime.GetAsync(TestHostFixture.FakeRuntime.ResponseId);
        await runtime.SaveAsync(new QuestionAppBuilder.Application.Responses.SaveResponseRequest(response.Id, response.Revision,
            new Dictionary<int, AnswerValue> { [1] = new(null, 700, null), [2] = new(null, 400, null) }));

        var initial = await GetAsync(host.Client, ResponsePath);
        var initialHtml = await initial.Content.ReadAsStringAsync();
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        initialHtml.Should().Contain(LiveMessage).And.NotContain(DeferredMessage);
        initialHtml.Should().Contain("aria-describedby=\"error-question-1\"");
        initialHtml.Should().NotContain("Complete the required fields", "ordinary GET uses partial evaluation, not submission completeness");

        var csrf = await CsrfAsync(host.Client, ResponsePath);
        var failingSave = await PostJsonSaveAsync(host.Client, csrf, new Dictionary<string, string>
        {
            ["CurrentCategoryIndex"] = "0", ["Answers[1].Number"] = "700", ["Answers[2].Number"] = "400"
        });
        failingSave.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var json = JsonDocument.Parse(await failingSave.Content.ReadAsStringAsync()))
        {
            var fragment = json.RootElement.GetProperty("liveContentHtml").GetString()!;
            fragment.Should().Contain(LiveMessage).And.NotContain(DeferredMessage);
            json.RootElement.GetProperty("liveValidationMessages").EnumerateArray().Select(item => item.GetString())
                .Should().Equal(LiveMessage);
        }

        var current = await CsrfAsync(host.Client, ResponsePath);
        var passingSave = await PostJsonSaveAsync(host.Client, current, new Dictionary<string, string>
        {
            ["CurrentCategoryIndex"] = "0", ["Answers[1].Number"] = "700", ["Answers[2].Number"] = "250"
        });
        passingSave.StatusCode.Should().Be(HttpStatusCode.OK);
        using var passed = JsonDocument.Parse(await passingSave.Content.ReadAsStringAsync());
        var passingFragment = passed.RootElement.GetProperty("liveContentHtml").GetString()!;
        passingFragment.Should().NotContain(LiveMessage).And.NotContain("qab-error-summary");
        passed.RootElement.GetProperty("liveValidationMessages").GetArrayLength().Should().Be(0);
        passingFragment.Should().NotContain("Complete the required fields", "live validation must not report unanswered required fields");

        var explicitErrorCsrf = await CsrfAsync(host.Client, ResponsePath);
        var malformed = await explicitErrorCsrf.PostAsync(host.Client, ResponsePath + "/Save", new Dictionary<string, string>
        {
            ["CurrentCategoryIndex"] = "0", ["Answers[1].Number"] = "not-a-number", ["Answers[2].Number"] = "1200"
        });
        malformed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var malformedHtml = await malformed.Content.ReadAsStringAsync();
        malformedHtml.Should().Contain("Enter a valid number.").And.NotContain(LiveMessage,
            "an explicit field parsing error on the same target takes precedence over its live rule error");
    }

    [Fact]
    public async Task Unchecked_rules_are_silent_during_editing_but_still_block_submission()
    {
        var runtime = new TestHostFixture.FakeRuntime { Graph = BuildGraph(showLive: false, includeDeferredRule: true) };
        await using var host = await TestHostFixture.StartAsync(runtime);
        await StartAsync(host);

        var csrf = await CsrfAsync(host.Client, ResponsePath);
        var partialSave = await PostJsonSaveAsync(host.Client, csrf, new Dictionary<string, string>
        {
            ["CurrentCategoryIndex"] = "0", ["Answers[1].Number"] = "700", ["Answers[2].Number"] = "400"
        });
        partialSave.StatusCode.Should().Be(HttpStatusCode.OK);
        using var partialJson = JsonDocument.Parse(await partialSave.Content.ReadAsStringAsync());
        var fragment = partialJson.RootElement.GetProperty("liveContentHtml").GetString()!;
        fragment.Should().NotContain(LiveMessage).And.NotContain(DeferredMessage).And.NotContain("qab-error-summary");

        var review = await GetAsync(host.Client, ResponsePath + "/Review");
        review.StatusCode.Should().Be(HttpStatusCode.OK);
        (await review.Content.ReadAsStringAsync()).Should().NotContain(LiveMessage).And.NotContain(DeferredMessage);

        var submitToken = await CsrfAsync(host.Client, ResponsePath + "/Review");
        var rejected = await submitToken.PostAsync(host.Client, ResponsePath + "/Submit", new Dictionary<string, string>
        {
            ["RequestId"] = Guid.NewGuid().ToString("D"), ["Answers[1].Number"] = "700", ["Answers[2].Number"] = "400"
        });
        rejected.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitHtml = await rejected.Content.ReadAsStringAsync();
        submitHtml.Should().Contain(LiveMessage);
        host.Runtime.SubmissionCount.Should().Be(0, "Show while answering affects presentation only, never submission validation");
    }

    private static ApplicationVersionGraph BuildGraph(bool showLive, bool includeDeferredRule)
    {
        var graph = TestHostFixture.FakeRuntime.BuildGraph();
        var category = graph.Categories[0];
        var secondCategory = graph.Categories[1];
        var first = category.Elements[0];
        var second = secondCategory.Elements[0];
        secondCategory.Elements.Clear();
        category.Elements.Add(second);
        first.Question!.AnswerType = AnswerType.Number;
        first.Question.IsRequired = true;
        second.Question!.AnswerType = AnswerType.Number;
        second.Question.IsRequired = true;

        var total = new CalculatedField { Name = "Total", CategoryKey = category.LogicalKey, Sources =
        [
            new ValueSource { Kind = SourceKind.Question, Key = first.LogicalKey },
            new ValueSource { Kind = SourceKind.Question, Key = second.LogicalKey }
        ] };
        CompositionRule Rule(string name, string message, bool live) => new()
        {
            Name = name, CategoryKey = category.LogicalKey, TargetQuestionKey = first.LogicalKey,
            Kind = RulePurpose.Validation, Message = message, ShowWhileAnswering = live,
            Checks = [new ValueCheck
            {
                Left = new ValueSource { Kind = SourceKind.Calculation, Key = total.Id },
                Comparison = Comparison.LessThanOrEqual,
                Right = new ValueSource { Kind = SourceKind.Constant, Value = 1000 }
            }]
        };
        graph.Version.CompositionJson = new CompositionDefinition
        {
            Calculations = [total],
            Rules = includeDeferredRule
                ? [Rule("Live ceiling", LiveMessage, showLive), Rule("Deferred ceiling", DeferredMessage, false)]
                : [Rule("Live ceiling", LiveMessage, showLive)]
        }.ToJson();
        return graph;
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
        if (string.IsNullOrEmpty(token)) token = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var revision = Regex.Match(html, "name=\\\"ExpectedRevision\\\" value=\\\"([^\\\"]+)\\\"").Groups[1].Value;
        return new Csrf(token, cookie, revision);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => client.GetAsync(Https(path));
    private static Task<HttpResponseMessage> PostJsonSaveAsync(HttpClient client, Csrf csrf, Dictionary<string, string> values)
        => csrf.PostAsync(client, ResponsePath + "/Save", values, json: true);
    private static Uri Https(string path) => new($"https://localhost{path}");

    private sealed record Csrf(string Token, string Cookie, string Revision)
    {
        public async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> values, bool json = false)
        {
            values["__RequestVerificationToken"] = Token;
            values.TryAdd("ExpectedRevision", Revision);
            using var request = new HttpRequestMessage(HttpMethod.Post, Https(path)) { Content = new FormUrlEncodedContent(values) };
            request.Headers.Add("Cookie", Cookie);
            if (json)
            {
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Add("X-Requested-With", "QuestionAppBuilder");
            }
            return await client.SendAsync(request);
        }
    }
}
