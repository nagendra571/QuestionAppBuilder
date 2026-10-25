using FluentAssertions;
using Microsoft.Playwright;
using QuestionAppBuilder.Runtime.Tests;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class RuntimeJourneyTests
{
    [Fact]
    public async Task Conditional_content_appears_after_autosave_and_on_review()
    {
        var graph = TestHostFixture.FakeRuntime.BuildGraph();
        var condition = new VisibilityCondition { Id = 98, ApplicationVersionId = graph.Version.Id, Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { Id = 98, SourceQuestionId = 1, Operator = ConditionOperator.Answered } } };
        graph.Version.VisibilityConditions.Add(condition);
        ((List<Element>)graph.Categories[0].Elements).Add(new Element { Id = 98, Kind = ElementKind.Content,
            Label = "Conditional instructions", SortOrder = 2, VisibilityConditionId = condition.Id, LogicalKey = Guid.NewGuid() });
        var unansweredCondition = new VisibilityCondition { Id = 97, ApplicationVersionId = graph.Version.Id, Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { Id = 97, SourceQuestionId = 1, Operator = ConditionOperator.Unanswered } } };
        graph.Version.VisibilityConditions.Add(unansweredCondition);
        ((List<Element>)graph.Categories[0].Elements).Add(new Element { Id = 97, Kind = ElementKind.Content,
            Label = "Unanswered-only instructions", SortOrder = 3, VisibilityConditionId = unansweredCondition.Id, LogicalKey = Guid.NewGuid() });
        var runtime = new TestHostFixture.FakeRuntime { Graph = graph };
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        (await page.Locator("main.qab-runtime").InnerTextAsync()).Should().NotContain("Conditional instructions");
        (await page.Locator("main.qab-runtime").InnerTextAsync()).Should().Contain("Unanswered-only instructions");
        await page.Locator("[name='Answers[1].Text']").FillAsync("show content");
        await page.GetByText("Conditional instructions", new() { Exact = true }).WaitForAsync();
        await page.GetByText("Unanswered-only instructions", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.Locator("[name='Answers[1].Text']").FillAsync("");
        await page.GetByText("Conditional instructions", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.Locator("[name='Answers[1].Text']").FillAsync("show content again");
        await page.GetByText("Conditional instructions", new() { Exact = true }).WaitForAsync();

        for (var question = 1; question <= 5; question++)
        {
            if (question > 1) await page.Locator($"[name='Answers[{question}].Text']").FillAsync($"answer {question}");
            if (question < 5)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new() { Name = $"Category {question + 1}" }).WaitForAsync();
            }
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        var review = page.Locator("main.qab-review");
        await review.GetByText("Conditional instructions", new() { Exact = true }).WaitForAsync();
        (await review.InnerTextAsync()).Should().Contain("Conditional instructions").And.NotContain("Unanswered-only instructions");
    }

    [Fact]
    public async Task Confirmation_total_and_answer_review_receipt_is_responsive_and_stacked()
    {
        var runtime = new TestHostFixture.FakeRuntime
        {
            Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: ResultVisibility.ConfirmationAndAnswerReview)
        };
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        for (var question = 1; question <= 5; question++)
        {
            await page.Locator($"[name='Answers[{question}].Text']").FillAsync($"Answer {question}");
            await page.GetByRole(AriaRole.Button, new() { Name = question < 5 ? "Save & Continue" : "Review your answers" }).ClickAsync();
            if (question < 5)
                await page.GetByRole(AriaRole.Heading, new() { Name = $"Category {question + 1}" }).WaitForAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Send application" }).ClickAsync();
        var receipt = page.Locator("main.qab-receipt");
        await receipt.WaitForAsync();

        var screenshotDirectory = Environment.GetEnvironmentVariable("QAB_UI_SCREENSHOTS");
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.ReloadAsync();
            await receipt.WaitForAsync();
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1], $"confirmation receipt must fit at {width}px");
            var receiptText = await receipt.InnerTextAsync();
            for (var category = 1; category <= 5; category++) receiptText.Should().Contain($"Category {category}");
            receiptText.Should().Contain("Answer 1").And.Contain("Application introduction").And.Contain("Supporter");
            (await receipt.Locator("[data-total-score='2']").CountAsync()).Should().Be(1);
            (await receipt.Locator(".qab-tabs").CountAsync()).Should().Be(0, "mode 4 keeps the whole review visible at once");
            (await receipt.Locator("form, input, textarea, select, button, script[src*='runtime.js']").CountAsync()).Should().Be(0);
            if (!string.IsNullOrWhiteSpace(screenshotDirectory))
            {
                Directory.CreateDirectory(screenshotDirectory);
                await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, $"confirmation-answer-review-{width}.png"), FullPage = true });
            }
        }
    }

    [Theory]
    [InlineData(SubmittedApplicationVisibility.WithoutScores, false)]
    [InlineData(SubmittedApplicationVisibility.WithScores, true)]
    public async Task Submitted_application_receipt_is_browse_only_and_fits_desktop_and_mobile(
        SubmittedApplicationVisibility visibility, bool hasResults)
    {
        var runtime = new TestHostFixture.FakeRuntime
        {
            Graph = TestHostFixture.FakeRuntime.BuildGraph(visibility: ResultVisibility.Total)
        };
        runtime.Graph.Version.SubmittedApplicationVisibility = visibility;
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        for (var question = 1; question <= 5; question++)
        {
            await page.Locator($"[name='Answers[{question}].Text']").FillAsync($"Answer {question}");
            await page.GetByRole(AriaRole.Button, new() { Name = question < 5 ? "Save & Continue" : "Review your answers" }).ClickAsync();
            if (question < 5)
                await page.GetByRole(AriaRole.Heading, new() { Name = $"Category {question + 1}" }).WaitForAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Send application" }).ClickAsync();
        await page.Locator("main.qab-receipt").WaitForAsync();

        var submittedUrl = new Uri(host.BaseAddress,
            $"/App/Responses/{TestHostFixture.FakeRuntime.ResponseId}/Submitted").ToString();
        var screenshotDirectory = Environment.GetEnvironmentVariable("QAB_UI_SCREENSHOTS");
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.GotoAsync(submittedUrl);
            await page.Locator("main.qab-receipt").WaitForAsync();
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1], $"receipt must fit at {width}px");
            var firstCategoryText = await page.Locator(".qab-receipt").InnerTextAsync();
            firstCategoryText.Should().Contain("Category 1").And.Contain("Answer 1").And.Contain("Application introduction");
            (await page.Locator("form, input, textarea, select, button").CountAsync()).Should().Be(0,
                "a submitted application can be browsed without editing or submitting it");
            (await page.Locator("script[src*='runtime.js']").CountAsync()).Should().Be(0);
            if (hasResults)
            {
                firstCategoryText.Should().Contain("Supporter").And.Contain("Summary").And.NotContain("+0 points");
                (await page.Locator("[data-total-score='2']").CountAsync()).Should().Be(1);
                var groupedAnswer = await page.Locator(".qab-group-table tbody tr td[data-column='Submitted answer']").First.InnerTextAsync();
                groupedAnswer.IndexOf("Question 1", StringComparison.Ordinal).Should().BeLessThan(groupedAnswer.IndexOf("Answer in the indicated field.", StringComparison.Ordinal));
                groupedAnswer.IndexOf("Answer in the indicated field.", StringComparison.Ordinal).Should().BeLessThan(groupedAnswer.IndexOf("Answer 1", StringComparison.Ordinal));
            }
            else
            {
                firstCategoryText.Should().NotContain("Supporter")
                    .And.NotContain("Points earned");
                (await page.Locator("[data-total-score]").CountAsync()).Should().Be(0);
            }

            if (!string.IsNullOrWhiteSpace(screenshotDirectory))
            {
                Directory.CreateDirectory(screenshotDirectory);
                await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, $"submitted-receipt-{visibility}-{width}.png"), FullPage = true });
            }
            await page.Locator(".qab-tabs a[href='?category=4']").ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Category 5" }).WaitForAsync();
            (await page.Locator(".qab-receipt").InnerTextAsync()).Should().Contain("Answer 5");
        }
    }

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
