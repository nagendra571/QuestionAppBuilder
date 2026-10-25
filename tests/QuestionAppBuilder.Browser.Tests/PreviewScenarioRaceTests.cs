using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class PreviewScenarioRaceTests
{
    [Fact]
    public async Task Successful_load_response_is_discarded_when_preview_input_changes_while_it_is_held()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await OpenPreviewAsync(host, page, applicationId);
        var (row, liveAnswer) = await SaveScenarioAsync(page, "Load race", "Stored scenario answer");

        var loadFetched = new TaskCompletionSource<IAPIResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/PreviewScenarios/Load/**", async route =>
        {
            var successfulResponse = await route.FetchAsync();
            loadFetched.TrySetResult(successfulResponse);
            await releaseLoad.Task;
            await route.FulfillAsync(new RouteFulfillOptions { Response = successfulResponse });
        });

        try
        {
            await row.GetByRole(AriaRole.Button, new() { Name = "Load answers", Exact = true }).ClickAsync();
            var fetched = await loadFetched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fetched.Status.Should().Be(200, "the held response must be a real successful load response");
            await liveAnswer.FillAsync("User edit while load is pending");
        }
        finally
        {
            releaseLoad.TrySetResult();
        }

        await page.Locator("[data-scenario-status]").GetByText("Load it again.", new() { Exact = false }).WaitForAsync();
        (await liveAnswer.InputValueAsync()).Should().Be("User edit while load is pending");
        var preview = page.Locator("[data-preview-result]");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Back", Exact = true }).ClickAsync();
        await preview.Locator("[data-preview-category='0']").WaitForAsync();
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
        var restoredCategory = preview.Locator("[data-preview-category='1']");
        await restoredCategory.WaitForAsync();
        var restoredId = await restoredCategory.Locator("[data-question-id]").First.GetAttributeAsync("data-question-id");
        (await restoredCategory.Locator($"[name='Answers[{restoredId}].Text']").InputValueAsync())
            .Should().Be("User edit while load is pending", "the preview answer map must keep the edit as well as the visible input");
    }

    [Fact]
    public async Task Double_click_during_held_save_stores_only_one_scenario()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await OpenPreviewAsync(host, page, applicationId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();
        await page.Locator("[data-preview-runtime-form]").WaitForAsync();
        await page.Locator("[data-scenario-name]").FillAsync("Exactly once");
        await page.Locator("[data-scenario-validity]").SelectOptionAsync("valid");

        var saveFetched = new TaskCompletionSource<IAPIResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveRequestCount = 0;
        await page.RouteAsync("**/PreviewScenarios/Save/**", async route =>
        {
            Interlocked.Increment(ref saveRequestCount);
            var successfulResponse = await route.FetchAsync();
            saveFetched.TrySetResult(successfulResponse);
            await releaseSave.Task;
            await route.FulfillAsync(new RouteFulfillOptions { Response = successfulResponse });
        });

        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Save current answers as a scenario", Exact = true }).DblClickAsync();
            var fetched = await saveFetched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fetched.Status.Should().Be(200, "the held request must have stored the scenario successfully");
        }
        finally
        {
            releaseSave.TrySetResult();
        }

        var list = page.Locator("[data-scenario-list]");
        await list.GetByText("Exactly once", new() { Exact = true }).WaitForAsync();
        saveRequestCount.Should().Be(1, "a second Save event must be ignored while the first request is in flight");
        (await list.Locator("li").CountAsync()).Should().Be(1);
    }

    private static async Task OpenPreviewAsync(SampleHostFixture host, IPage page, int applicationId)
    {
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
    }

    private static async Task<(ILocator Row, ILocator Answer)> SaveScenarioAsync(IPage page, string name, string savedAnswer)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        var firstQuestion = preview.Locator("[data-preview-category='0'] [data-question-id]").First;
        await firstQuestion.WaitForAsync();
        var firstId = await firstQuestion.GetAttributeAsync("data-question-id");
        await preview.Locator($"[name='Answers[{firstId}].Number']").FillAsync("9");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
        var secondQuestion = preview.Locator("[data-preview-category='1'] [data-question-id]").First;
        await secondQuestion.WaitForAsync();
        var secondId = await secondQuestion.GetAttributeAsync("data-question-id");
        var answer = preview.Locator($"[name='Answers[{secondId}].Text']");
        await answer.FillAsync(savedAnswer);

        await page.Locator("[data-scenario-name]").FillAsync(name);
        await page.Locator("[data-scenario-validity]").SelectOptionAsync("valid");
        var saved = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/PreviewScenarios/Save/", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save current answers as a scenario", Exact = true }).ClickAsync();
        (await saved).Status.Should().Be(200);
        var list = page.Locator("[data-scenario-list]");
        await list.GetByText(name, new() { Exact = true }).WaitForAsync();
        var row = list.Locator("li").Filter(new() { HasText = name });
        return (row, answer);
    }
}
