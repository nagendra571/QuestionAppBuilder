using FluentAssertions;
using Microsoft.Playwright;
using QuestionAppBuilder.Runtime.Tests;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class RuntimeJourneyTests
{
    [Fact]
    public async Task Pending_autosave_preserves_later_edit_renders_new_branch_and_honors_continue_click()
    {
        var graph = TestHostFixture.FakeRuntime.BuildGraph();
        var condition = new VisibilityCondition { Id = 99, ApplicationVersionId = graph.Version.Id, Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { Id = 99, SourceQuestionId = 1, Operator = ConditionOperator.Answered } } };
        graph.Version.VisibilityConditions.Add(condition);
        graph.Categories[0].Elements.Add(new Element { Id = 99, Kind = ElementKind.Question, Label = "Branch follow-up", SortOrder = 2,
            LogicalKey = Guid.NewGuid(), VisibilityConditionId = condition.Id,
            Question = new Question { Id = 99, AnswerType = AnswerType.Textbox } });
        var runtime = new TestHostFixture.FakeRuntime { Graph = graph };
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await page.Locator("[data-response-id]").EvaluateAsync("element => element.dataset.saveDelay = '1'");

        var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        await page.RouteAsync("**/Save", async route =>
        {
            if (Interlocked.Increment(ref saveCount) == 1)
            {
                firstSaveStarted.TrySetResult();
                await releaseFirstSave.Task;
            }
            await route.ContinueAsync();
        });
        var answer = page.Locator("[name='Answers[1].Text']");
        await answer.FillAsync("first edit");
        await firstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await answer.FillAsync("later edit during save");
        releaseFirstSave.TrySetResult();

        await page.Locator("[name='Answers[99].Text']").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        (await answer.InputValueAsync()).Should().Be("later edit during save");
        saveCount.Should().BeGreaterThanOrEqualTo(2);
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved just now." }).WaitForAsync(new() { Timeout = 5000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Category 2" }).WaitForAsync();
        (await runtime.GetAsync(TestHostFixture.FakeRuntime.ResponseId)).Answers[1].Text.Should().Be("later edit during save");
    }

    [Fact]
    public async Task Continue_click_during_inflight_autosave_is_not_dropped()
    {
        var runtime = new TestHostFixture.FakeRuntime();
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await page.Locator("[data-response-id]").EvaluateAsync("element => element.dataset.saveDelay = '1'");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Save", async route => { started.TrySetResult(); await release.Task; await route.ContinueAsync(); });
        await page.Locator("[name='Answers[1].Text']").FillAsync("saved before navigation");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        release.TrySetResult();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Category 2" }).WaitForAsync(new() { Timeout = 10000 });
        (await runtime.GetAsync(TestHostFixture.FakeRuntime.ResponseId)).Answers[1].Text.Should().Be("saved before navigation");
    }

    [Fact]
    public async Task Newly_visible_final_category_replaces_review_button_during_pending_save()
    {
        var graph = TestHostFixture.FakeRuntime.BuildGraph();
        var condition = new VisibilityCondition { Id = 99, ApplicationVersionId = graph.Version.Id, Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { Id = 99, SourceQuestionId = 5, Operator = ConditionOperator.Answered } } };
        var category = new Category { Id = 99, Name = "Conditional category", SortOrder = 6, VisibilityConditionId = condition.Id,
            Elements = { new Element { Id = 99, Kind = ElementKind.Question, Label = "Branch question", SortOrder = 1,
                LogicalKey = Guid.NewGuid(), Question = new Question { Id = 99, AnswerType = AnswerType.Textbox, IsRequired = true } } } };
        ((List<Category>)graph.Categories).Add(category);
        ((List<VisibilityCondition>)graph.VisibilityConditions).Add(condition);
        var runtime = new TestHostFixture.FakeRuntime { Graph = graph };
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        for (var question = 1; question < 5; question++)
        {
            await page.Locator($"[name='Answers[{question}].Text']").FillAsync($"answer {question}");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = $"Category {question + 1}" }).WaitForAsync();
        }

        await page.Locator("[data-response-id]").EvaluateAsync("element => element.dataset.saveDelay = '1'");
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Save", async route => { saveStarted.TrySetResult(); await releaseSave.Task; await route.ContinueAsync(); });
        await page.Locator("[name='Answers[5].Text']").FillAsync("reveal the final category");
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        releaseSave.TrySetResult();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Conditional category" }).WaitForAsync(new() { Timeout = 10000 });
        await page.Locator("[name='Answers[99].Text']").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        (await runtime.GetAsync(TestHostFixture.FakeRuntime.ResponseId)).Answers[5].Text.Should().Be("reveal the final category");
    }

    [Fact]
    public async Task Respondent_can_save_resume_and_submit_in_a_real_browser()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        (await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Core/js/runtime.js')).status")).Should().Be(200);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await page.Locator("[name='Answers[1].Text']").FillAsync("persist me");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved just now." }).WaitForAsync(new() { Timeout = 5000 });

        await context.CloseAsync();
        var resumedContext = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var resumedPage = await resumedContext.NewPageAsync();
        await resumedPage.GotoAsync(new Uri(host.BaseAddress, $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}").ToString());
        (await resumedPage.Locator("[name='Answers[1].Text']").InputValueAsync()).Should().Be("persist me");
        for (var question = 1; question <= 5; question++)
        {
            var input = resumedPage.Locator($"[name='Answers[{question}].Text']");
            if (question > 1) await input.FillAsync($"answer {question}");
            if (question < 5) await resumedPage.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
            else await resumedPage.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        }
        await resumedPage.GetByRole(AriaRole.Button, new() { Name = "Send application" }).ClickAsync();
        await resumedPage.GetByRole(AriaRole.Heading, new() { Name = "Thank you" }).WaitForAsync();
        host.Runtime.SubmissionCount.Should().Be(1);
    }

    [Fact]
    public async Task Failed_autosave_keeps_typed_value_and_blocks_navigation()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await page.Locator("[data-response-id]").EvaluateAsync("element => element.dataset.saveDelay = '5000'");
        await page.RouteAsync("**/Save", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 503,
            ContentType = "text/plain",
            Body = "Injected save failure"
        }));
        var failedSaveResponseCount = 0;
        page.Response += (_, response) =>
        {
            if (response.Url.EndsWith("/Save", StringComparison.Ordinal) && response.Status == 503) failedSaveResponseCount++;
        };
        var failedSaveResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/Save", StringComparison.Ordinal) && response.Status == 503,
            new PageWaitForResponseOptions { Timeout = 10000 });
        await page.Locator("[name='Answers[1].Text']").FillAsync("keep this value");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        (await failedSaveResponse).Status.Should().Be(503);
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "could not be saved" }).WaitForAsync(new() { Timeout = 5000 });
        await page.WaitForTimeoutAsync(500);
        failedSaveResponseCount.Should().Be(1, "a failed save is not retried until the respondent deliberately edits or retries");
        (await page.Locator("[name='Answers[1].Text']").InputValueAsync()).Should().Be("keep this value");
        (await page.Locator(".qab-progress").InnerTextAsync()).Should().Contain("Category 1 of");
    }
}
