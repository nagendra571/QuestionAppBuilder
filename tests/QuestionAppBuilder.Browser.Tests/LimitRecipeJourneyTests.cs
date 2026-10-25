using FluentAssertions;
using Microsoft.Playwright;
using System.Text.Json;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class LimitRecipeJourneyTests
{
    [Fact]
    public async Task Limit_recipe_previews_exact_decimal_and_adds_only_after_explicit_confirmation()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedCompositionApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{applicationId}").ToString());

        var savePosts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Composition/Save", StringComparison.Ordinal))
                Interlocked.Increment(ref savePosts);
        };

        var recipe = page.Locator("[data-limit-recipe]");
        await recipe.GetByRole(AriaRole.Button, new() { Name = "Create a limit", Exact = true }).ClickAsync();
        await recipe.Locator("[data-limit-name]").FillAsync("Combined income ceiling");
        await recipe.Locator("[data-limit-source-name='Requested amount']").CheckAsync();
        await recipe.Locator("[data-limit-source-name='Matching amount']").CheckAsync();
        await recipe.Locator("[data-limit-target]").SelectOptionAsync(new SelectOptionValue { Label = "Requested amount · Budget" });
        await recipe.Locator("[data-limit-message]").FillAsync("Combined amounts must stay within the ceiling.");
        await recipe.Locator("[data-limit-live]").CheckAsync();
        (await page.EvaluateAsync<bool>("() => { const e = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(e); return e.defaultPrevented; }"))
            .Should().BeTrue("an unfinished recipe must be protected from accidental navigation");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        await recipe.GetByText("Add this limit to configuration or discard the recipe before saving.", new() { Exact = true }).WaitForAsync();
        savePosts.Should().Be(0, "an unfinished recipe must not be saved as an incomplete configuration");
        var maximum = recipe.Locator("[data-limit-maximum]");
        var previewButton = recipe.GetByRole(AriaRole.Button, new() { Name = "Preview limit", Exact = true });
        var preview = recipe.Locator("[data-limit-preview-result]");

        await maximum.FillAsync("9007199254740993");
        await previewButton.ClickAsync();
        await recipe.GetByText("cannot be represented exactly", new() { Exact = false }).WaitForAsync();
        (await recipe.GetByRole(AriaRole.Button, new() { Name = "Add this limit to configuration", Exact = true }).IsDisabledAsync()).Should().BeTrue();

        await maximum.FillAsync("12.50");
        await previewButton.ClickAsync();
        await preview.GetByText("12.5", new() { Exact = false }).WaitForAsync();
        (await preview.InnerTextAsync()).Should().Contain("Sum of Requested amount and Matching amount")
            .And.Contain("Combined income ceiling total")
            .And.Contain("will appear in Budget")
            .And.Contain("only while Requested amount is visible")
            .And.Contain("Combined amounts must stay within the ceiling.")
            .And.Contain("live feedback")
            .And.Contain("displayed total is rounded to two decimal places")
            .And.Contain("Validation compares the full value")
            .And.Contain("sequential forward navigation and final submission")
            .And.Contain("Hidden or missing numeric answers contribute 0");
        await page.SetViewportSizeAsync(1440, 1000);
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-limit-recipe-1440.png"), FullPage = true });
        await page.SetViewportSizeAsync(375, 1000);
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
        (await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth")).Should().BeTrue("the recipe should fit a mobile viewport without horizontal scrolling");
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-limit-recipe-375.png"), FullPage = true });
        await page.SetViewportSizeAsync(1440, 1000);

        await maximum.FillAsync("0.0000000000000000000000000001");
        await previewButton.ClickAsync();
        await preview.GetByText("0.0000000000000000000000000001", new() { Exact = false }).WaitForAsync();
        await maximum.FillAsync("0.0000000000000000000123");
        await previewButton.ClickAsync();
        await preview.GetByText("0.0000000000000000000123", new() { Exact = false }).WaitForAsync();
        await recipe.GetByRole(AriaRole.Button, new() { Name = "Add this limit to configuration", Exact = true }).ClickAsync();

        savePosts.Should().Be(0, "previewing and staging the recipe must not write the saved configuration");
        var ruleCard = page.Locator(".composition-item[data-rule-id]").Filter(new() { HasText = "Combined income ceiling" });
        await ruleCard.WaitForAsync();
        (await ruleCard.EvaluateAsync<bool>("card => card.open")).Should().BeTrue("the new rule is opened for review");
        (await ruleCard.InnerTextAsync()).Should().Contain("1.23e-20", "the editor summary must not round a valid tiny decimal to zero");
        (await page.Locator("[data-composition-status]").InnerTextAsync()).ToLowerInvariant().Should().Contain("unsaved");

        await recipe.GetByRole(AriaRole.Button, new() { Name = "Create a limit", Exact = true }).ClickAsync();
        await recipe.Locator("[data-limit-name]").FillAsync("Discard this unused recipe");
        await recipe.GetByRole(AriaRole.Button, new() { Name = "Discard limit", Exact = true }).ClickAsync();
        (await page.Locator("[data-composition-status]").InnerTextAsync()).ToLowerInvariant().Should().Contain("unsaved", "discarding the recipe must preserve the staged rule and calculation");

        var saveResponse = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Composition/Save", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        (await saveResponse).Status.Should().Be(200);
        savePosts.Should().Be(1);
        await page.ReloadAsync();
        using var persisted = JsonDocument.Parse(await page.Locator("[data-composition-definition]").TextContentAsync()
            ?? throw new InvalidOperationException("Saved composition JSON was not rendered."));
        persisted.RootElement.GetProperty("Calculations").GetArrayLength().Should().Be(1);
        persisted.RootElement.GetProperty("Rules").GetArrayLength().Should().Be(1);
        persisted.RootElement.GetProperty("Rules")[0].GetProperty("Checks")[0].GetProperty("Right").GetProperty("Value")
            .GetDecimal().Should().Be(0.0000000000000000000123m, "the accepted recipe maximum must round-trip through the saved composition exactly");
    }
}
