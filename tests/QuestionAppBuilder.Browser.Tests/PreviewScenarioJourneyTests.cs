using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class PreviewScenarioJourneyTests
{
    [Fact]
    public async Task Saved_scenarios_run_pass_and_fail_preserve_inputs_survive_reload_and_delete_safely()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
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
        var secondAnswer = preview.Locator($"[name='Answers[{secondId}].Text']");
        await secondAnswer.FillAsync("Purpose saved in scenario");

        var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save current answers as a scenario", Exact = true });
        await page.Locator("[data-scenario-name]").FillAsync("  Complete application  ");
        await page.Locator("[data-scenario-validity]").SelectOptionAsync("valid");
        await page.Locator("[data-scenario-total]").FillAsync("0");

        var saveResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/PreviewScenarios/Save/", StringComparison.Ordinal));
        await saveButton.ClickAsync();
        (await saveResponseTask).Status.Should().Be(200);
        (await page.Locator("[data-scenario-name]").InputValueAsync()).Should().Be("Complete application", "successful save canonicalizes the trimmed name");

        var scenarioList = page.Locator("[data-scenario-list]");
        await scenarioList.GetByText("Complete application", new() { Exact = true }).WaitForAsync();
        var row = scenarioList.Locator("li").Filter(new() { HasText = "Complete application" });
        await row.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        var results = page.Locator("[data-scenario-results]");
        await results.GetByText("Passed: Complete application", new() { Exact = true }).WaitForAsync();
        (await scenarioList.InnerTextAsync()).Should().Contain("Load answers").And.Contain("Edit name/expectation").And.Contain("Delete");
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue($"preview scenario controls fit at {width}px");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-preview-scenarios-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1280, 900);

        await secondAnswer.FillAsync("Unsaved interactive answer");
        await row.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await results.GetByText("Passed: Complete application", new() { Exact = true }).WaitForAsync();
        (await secondAnswer.InputValueAsync()).Should().Be("Unsaved interactive answer", "running saved scenarios does not replace the interactive preview answers");

        await row.GetByRole(AriaRole.Button, new() { Name = "Edit name/expectation", Exact = true }).ClickAsync();
        await page.Locator("[data-scenario-status]").GetByText("Editing the saved name and expectations.").WaitForAsync();
        (await secondAnswer.InputValueAsync()).Should().Be("Unsaved interactive answer", "editing metadata must not load or replace answers");
        await page.Locator("[data-scenario-name]").FillAsync("Renamed application");
        await page.GetByRole(AriaRole.Button, new() { Name = "Update name/expectation", Exact = true }).ClickAsync();
        await scenarioList.GetByText("Renamed application", new() { Exact = true }).WaitForAsync();

        row = scenarioList.Locator("li").Filter(new() { HasText = "Renamed application" });
        await row.GetByRole(AriaRole.Button, new() { Name = "Load answers", Exact = true }).ClickAsync();
        var loadedFirst = preview.Locator("[data-preview-category='0'] [data-question-id]").First;
        await loadedFirst.WaitForAsync();
        var loadedFirstId = await loadedFirst.GetAttributeAsync("data-question-id");
        (await preview.Locator($"[name='Answers[{loadedFirstId}].Number']").InputValueAsync()).Should().Be("9");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
        var loadedSecond = preview.Locator("[data-preview-category='1'] [data-question-id]").First;
        var loadedSecondId = await loadedSecond.GetAttributeAsync("data-question-id");
        var restoredAnswer = preview.Locator($"[name='Answers[{loadedSecondId}].Text']");
        (await restoredAnswer.InputValueAsync()).Should().Be("Purpose saved in scenario", "metadata-only update must preserve the stored answer set");

        await page.GetByRole(AriaRole.Button, new() { Name = "Clear scenario fields", Exact = true }).ClickAsync();
        await page.Locator("[data-scenario-name]").FillAsync("Expected one point");
        await page.Locator("[data-scenario-validity]").SelectOptionAsync("valid");
        await page.Locator("[data-scenario-total]").FillAsync("1");
        var secondSave = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/PreviewScenarios/Save/", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save current answers as a scenario", Exact = true }).ClickAsync();
        (await secondSave).Status.Should().Be(200);
        await scenarioList.GetByText("Expected one point", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run all scenarios", Exact = true }).ClickAsync();
        await results.GetByText("Passed: Renamed application", new() { Exact = true }).WaitForAsync();
        await results.GetByText("Failed: Expected one point", new() { Exact = true }).WaitForAsync();
        (await results.InnerTextAsync()).Should().Contain("Expected: validity valid; total 1 points.").And.Contain("Actual: valid; 0 points.");

        await page.ReloadAsync();
        scenarioList = page.Locator("[data-scenario-list]");
        await scenarioList.GetByText("Renamed application", new() { Exact = true }).WaitForAsync();
        await scenarioList.GetByText("Expected one point", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run all scenarios", Exact = true }).ClickAsync();
        results = page.Locator("[data-scenario-results]");
        await results.GetByText("Failed: Expected one point", new() { Exact = true }).WaitForAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        var displayName = page.Locator("[name='Version.DisplayName']");
        var draftSave = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.EndsWith("/Save", StringComparison.Ordinal));
        await displayName.FillAsync("Scenario stale preview draft");
        await displayName.BlurAsync();
        (await draftSave).Status.Should().Be(200);
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        results = page.Locator("[data-scenario-results]");
        (await results.GetAttributeAsync("data-stale")).Should().Be("true");
        await results.GetByText("The draft changed after these results.", new() { Exact = false }).WaitForAsync();

        var deleteDialog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Dialog += async (_, dialog) => { await dialog.AcceptAsync(); deleteDialog.TrySetResult(); };
        scenarioList = page.Locator("[data-scenario-list]");
        var staleRow = scenarioList.Locator("li").Filter(new() { HasText = "Expected one point" });
        var deleteResponse = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/PreviewScenarios/Delete/", StringComparison.Ordinal));
        await staleRow.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await deleteDialog.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await deleteResponse).Status.Should().Be(200);
        await scenarioList.GetByText("Expected one point", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await scenarioList.InnerTextAsync()).Should().Contain("Renamed application");

        (await host.CountResponsesAsync()).Should().Be(0);
        (await host.CountSubmissionsAsync()).Should().Be(0);
    }
}
