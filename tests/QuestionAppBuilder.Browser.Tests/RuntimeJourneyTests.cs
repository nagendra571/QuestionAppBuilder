using FluentAssertions;
using Microsoft.Playwright;
using QuestionAppBuilder.Runtime.Tests;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class RuntimeJourneyTests
{
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
        await page.RouteAsync("**/Save", route => route.AbortAsync());
        await page.Locator("[name='Answers[1].Text']").FillAsync("keep this value");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "could not be saved" }).WaitForAsync(new() { Timeout = 5000 });
        (await page.Locator("[name='Answers[1].Text']").InputValueAsync()).Should().Be("keep this value");
        (await page.Locator(".qab-progress").InnerTextAsync()).Should().Contain("Category 1 of");
    }
}
