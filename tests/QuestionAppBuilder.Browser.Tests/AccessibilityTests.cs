using Deque.AxeCore.Playwright;
using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class AccessibilityTests
{
    [Fact]
    public async Task Keyboard_and_320px_reflow_preserve_all_actions()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, ViewportSize = new ViewportSize { Width = 320, Height = 780 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.Keyboard.PressAsync("Tab");
        var focusedTag = await page.EvaluateAsync<string>("document.activeElement?.tagName ?? ''");
        focusedTag.Should().Be("A", "the host brand link is the first visible keyboard target");
        var focusStyle = await page.EvaluateAsync<string>("getComputedStyle(document.activeElement).outlineStyle");
        focusStyle.Should().NotBe("none");
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, window.innerWidth]");
        dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Category 2" }).CountAsync();
    }

    [Fact]
    public async Task Respondent_pages_have_no_axe_wcag_aa_violations()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());

        var landing = await page.RunAxe();
        landing.Violations.Should().BeEmpty("the questionnaire landing page must meet WCAG 2.1 AA automated checks");
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        var form = await page.RunAxe();
        form.Violations.Should().BeEmpty("the respondent answer form must meet WCAG 2.1 AA automated checks");
    }

    [Fact]
    public async Task Designer_workspace_exposes_one_banner_without_duplicate_landmark_violations()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedResponsiveAuthoringApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}").ToString());

        (await page.GetByRole(AriaRole.Banner).CountAsync()).Should().Be(1);
        var results = await page.RunAxe();
        var violationDetails = System.Text.Json.JsonSerializer.Serialize(results.Violations);
        results.Violations.Select(violation => violation.Id).Should()
            .NotContain("landmark-no-duplicate-banner", "violations: {0}", violationDetails)
            .And.NotContain("landmark-unique", "violations: {0}", violationDetails);
    }
}
