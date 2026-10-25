using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class WorkspaceHelpTests
{
    [Fact]
    public async Task Help_explains_live_choices_supports_keyboard_and_touch_and_does_not_save_when_opened()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}").ToString());
        var posts = new List<string>();
        page.Request += (_, request) => { if (request.Method == "POST") posts.Add(request.Url); };
        await page.Locator(".aw-outline-element").First.ClickAsync();
        await page.Locator(".aw-element:visible .aw-disclosure").First.Locator("summary").ClickAsync();
        var scoringHelp = page.GetByRole(AriaRole.Button, new() { Name = "Help for Scoring mode", Exact = true });
        await scoringHelp.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var openHelp = page.Locator(".aw-field-help:visible");
        await openHelp.WaitForAsync();
        (await openHelp.InnerTextAsync()).Should().Contain("5 points").And.Contain("0 points").And.Contain("distinct order numbers");
        await page.Keyboard.PressAsync("Escape");
        (await scoringHelp.GetAttributeAsync("aria-expanded")).Should().Be("false");
        (await scoringHelp.EvaluateAsync<bool>("element => document.activeElement === element")).Should().BeTrue();

        await page.Locator(".aw-outline-action[href='#question-assets']").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Help for Rule family", Exact = true }).ClickAsync();
        (await openHelp.InnerTextAsync()).Should().Contain("Presence").And.Contain("Selection").And.Contain("Numeric range");
        await page.GetByRole(AriaRole.Button, new() { Name = "Help for Membership", Exact = true }).ClickAsync();
        (await openHelp.CountAsync()).Should().Be(1);
        (await openHelp.InnerTextAsync()).Should().Contain("Extra selected options are allowed").And.Contain("only once");
        await page.Locator("[name='membershipOperator']").SelectOptionAsync("AllOf");
        (await page.Locator("#question-assets .aw-field-hint").AllTextContentsAsync()).Should().Contain("Every target option must be selected; extras are allowed.");
        posts.Should().BeEmpty("help interactions and unfinished add-rule fields must not submit or autosave the definition");
        foreach (var width in new[] { 1440, 375, 320 })
        {
            await page.SetViewportSizeAsync(width, 900);
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-help-membership-{width}.png"), FullPage = true });
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Close help for Membership", Exact = true }).ClickAsync();
        (await openHelp.CountAsync()).Should().Be(0);

        await page.SetViewportSizeAsync(1440, 900);
        await page.GetByRole(AriaRole.Link, new() { Name = "Visibility", Exact = true }).ClickAsync();
        foreach (var check in new[] { (Operator: "GreaterThanOrEqual", Number: "3"), (Operator: "LessThanOrEqual", Number: "10") })
        {
            var form = page.Locator("#add-condition-form");
            var target = await form.Locator("[name='elementId'] option").Last.GetAttributeAsync("value");
            await form.Locator("[name='elementId']").SelectOptionAsync(target!);
            await form.Locator("[name='conditionOperator']").SelectOptionAsync(check.Operator);
            await form.Locator("[name='numberValue']").FillAsync(check.Number);
            var reload = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/") && !response.Url.Contains("/Readiness"));
            await form.GetByRole(AriaRole.Button, new() { Name = "Add condition to selected question", Exact = true }).ClickAsync();
            await reload;
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
        await page.Locator(".aw-outline-element").Last.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Help for Show when", Exact = true }).ClickAsync();
        (await openHelp.InnerTextAsync()).Should().Contain("Always show").And.Contain("Every check must match")
            .And.Contain("Requested amount: is at least 3").And.Contain("Requested amount: is at most 10");
        errors.Should().BeEmpty();
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-help-visibility-1440.png"), FullPage = true });
    }

    [Fact]
    public async Task Content_visibility_help_describes_its_condition_and_dynamic_hide_behavior()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedContentVisibilityPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}").ToString());
        await page.SetViewportSizeAsync(1440, 900);
        await page.Locator(".aw-outline-element").Filter(new() { HasText = "Conditional preview instructions" }).ClickAsync();
        var contentElement = page.Locator(".aw-element:visible").Filter(new() { HasText = "Conditional preview instructions" });
        await contentElement.WaitForAsync();
        (await contentElement.CountAsync()).Should().Be(1);
        await contentElement.GetByRole(AriaRole.Button, new() { Name = "Help for Show when", Exact = true }).ClickAsync();
        var help = page.Locator(".aw-field-help:visible");
        var contentHelp = await help.InnerTextAsync();
        contentHelp.Should().Contain("category").And.Contain("own Show when condition").And.Contain("Hide when unavailable")
            .And.Contain("Condition 1: all checks").And.Contain("Visibility trigger: has an answer")
            .And.NotContain("does not currently hide static content");
        errors.Should().BeEmpty();
    }
}
