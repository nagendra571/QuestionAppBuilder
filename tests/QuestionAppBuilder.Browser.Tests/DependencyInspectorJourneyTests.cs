using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class DependencyInspectorJourneyTests
{
    [Fact]
    public async Task Inspector_flushes_edits_rejects_a_stale_read_and_links_to_the_composition_pane()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDependencyInspectorApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var errors = new List<string>();
        var requestOrder = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.EndsWith("/Save")) requestOrder.Add("save");
            if (request.Method == "GET" && request.Url.Contains("/Dependencies?", StringComparison.Ordinal)) requestOrder.Add("dependencies");
        };
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.SetViewportSizeAsync(1440, 900);

        await page.Locator(".aw-outline-element").Filter(new() { HasText = "Annual income" }).ClickAsync();
        var question = page.Locator(".aw-element[data-question-id]:not([data-question-id=''])").First;
        var panel = question.Locator("[data-dependency-panel]");
        var summary = panel.GetByText("Used by and related items", new() { Exact = true });
        var intercepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Dependencies*", async route =>
        {
            intercepted.TrySetResult();
            await continueRead.Task;
            await route.ContinueAsync();
        });

        var firstSaveTask = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.EndsWith("/Save"));
        await question.Locator("input[name$='.Label']").First.FillAsync("Annual income pending");
        await summary.ClickAsync();
        (await firstSaveTask).Ok.Should().BeTrue();
        await intercepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        requestOrder.IndexOf("save").Should().BeLessThan(requestOrder.IndexOf("dependencies"), "the inspector must save before reading the pinned snapshot");
        var label = question.Locator("input[name$='.Label']").First;
        var secondSaveTask = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.EndsWith("/Save"));
        await label.FillAsync("Annual income revised");
        var saved = await secondSaveTask;
        saved.Ok.Should().BeTrue();
        var staleResponseTask = page.WaitForResponseAsync(response => response.Url.Contains("/Dependencies?", StringComparison.Ordinal));
        continueRead.TrySetResult();
        var staleResponse = await staleResponseTask;
        staleResponse.Status.Should().Be(409);
        (await panel.Locator("[data-dependency-results]").InnerTextAsync()).Should().NotContain("Income subtotal");

        await page.UnrouteAsync("**/Dependencies*");
        await summary.ClickAsync();
        var freshReadTask = page.WaitForResponseAsync(response => response.Url.Contains("/Dependencies?", StringComparison.Ordinal) && response.Status == 200);
        await summary.ClickAsync();
        (await freshReadTask).Status.Should().Be(200);
        var results = panel.Locator("[data-dependency-results]");
        await results.GetByText("Income subtotal", new() { Exact = false }).WaitForAsync();
        var text = await results.InnerTextAsync();
        text.Should().Contain("Applicant data").And.Contain("Income review").And.Contain("Income display").And.Contain("Income note");
        var dynamicTextLink = results.Locator("a[href$='#DynamicText']").First;
        (await dynamicTextLink.GetAttributeAsync("href")).Should().Contain($"/ConfigureApp/Composition/Index/{applicationId}#DynamicText");
        requestOrder.Count(entry => entry == "save").Should().Be(2);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-dependency-inspector-1440.png"), FullPage = true });
        await page.SetViewportSizeAsync(375, 900);
        (await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]"))[0].Should().BeLessThanOrEqualTo(375);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-dependency-inspector-375.png"), FullPage = true });
        await page.SetViewportSizeAsync(320, 900);
        (await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]"))[0].Should().BeLessThanOrEqualTo(320);
        await page.SetViewportSizeAsync(1440, 900);

        await dynamicTextLink.ClickAsync();
        await page.WaitForURLAsync($"**/ConfigureApp/Composition/Index/{applicationId}#DynamicText");
        (await page.Locator("[data-pane='DynamicText']").GetAttributeAsync("aria-pressed")).Should().Be("true");
        errors.Should().BeEmpty();
    }
}
