using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class ResponsiveSurfaceTests
{
    [Fact]
    public async Task Populated_authoring_pages_fit_mobile_viewports_and_keep_actions_reachable()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedResponsiveAuthoringApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        var screenshotDirectory = Environment.GetEnvironmentVariable("QAB_UI_SCREENSHOTS");
        foreach (var width in new[] { 320, 375, 768, 1440 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}").ToString());
            var scoringTable = page.Locator("#scoring .aw-table");
            await scoringTable.WaitForAsync();
            (await page.Locator("#scoring table tbody tr").CountAsync()).Should().BeGreaterThan(0);
            var workspaceDimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            var overflowElements = await page.EvaluateAsync<string>("""() => JSON.stringify([...document.querySelectorAll('*')].map(e => { const r=e.getBoundingClientRect(); return {tag:e.tagName,id:e.id,cls:typeof e.className==='string'?e.className:'',text:(e.innerText||'').slice(0,48),left:Math.round(r.left),right:Math.round(r.right),width:Math.round(r.width),scrollWidth:e.scrollWidth,clientWidth:e.clientWidth}; }).filter(e => e.right > innerWidth + 1 || e.left < -1).sort((a,b)=>b.right-a.right).slice(0,12))""");
            workspaceDimensions[0].Should().BeLessThanOrEqualTo(workspaceDimensions[1], $"workspace must not spill at {width}px; offenders: {overflowElements}");

            if (width <= 375)
            {
                var scoringRegion = page.Locator("#scoring .aw-table-scroll");
                var regionWidths = await scoringRegion.EvaluateAsync<double[]>("e => [e.clientWidth, e.scrollWidth]");
                regionWidths[1].Should().BeGreaterThan(regionWidths[0], "the table should remain usable in its own horizontal scroll region");
                await scoringRegion.FocusAsync();
                for (var step = 0; step < 12; step++) await page.Keyboard.PressAsync("ArrowRight");
                (await scoringRegion.EvaluateAsync<double>("e => e.scrollLeft")).Should().BeGreaterThan(0);
            }
            if (!string.IsNullOrWhiteSpace(screenshotDirectory) && width is 375 or 768)
            {
                Directory.CreateDirectory(screenshotDirectory);
                await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, $"designer-workspace-{width}.png"), FullPage = true });
            }

            await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Applications/Detail/{id}").ToString());
            var detailDimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            detailDimensions[0].Should().BeLessThanOrEqualTo(detailDimensions[1], $"detail page must not spill at {width}px");
            var removeButtons = page.Locator(".cv-cact button.cv-danger, .cv-el-remove button");
            (await removeButtons.CountAsync()).Should().BeGreaterThan(0);
            for (var index = 0; index < await removeButtons.CountAsync(); index++)
            {
                var bounds = await removeButtons.Nth(index).BoundingBoxAsync();
                bounds.Should().NotBeNull();
                bounds!.X.Should().BeGreaterThanOrEqualTo(0);
                (bounds.X + bounds.Width).Should().BeLessThanOrEqualTo(width);
            }

            await page.Locator(".cv-rename summary").First.ClickAsync();
            await AssertFormFitsAsync(page.Locator(".cv-rename-form"), width);
            await page.Locator(".cv-rename summary").First.ClickAsync();
            var addQuestion = page.Locator(".cv-add-form").Nth(0);
            await addQuestion.Locator("summary").ClickAsync();
            await AssertFormFitsAsync(addQuestion.Locator("form"), width);
            await addQuestion.Locator("summary").ClickAsync();
            var addContent = page.Locator(".cv-add-form").Nth(1);
            await addContent.Locator("summary").ClickAsync();
            await AssertFormFitsAsync(addContent.Locator("form"), width);
            if (!string.IsNullOrWhiteSpace(screenshotDirectory) && width is 375 or 768)
            {
                Directory.CreateDirectory(screenshotDirectory);
                await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, $"designer-detail-{width}.png"), FullPage = true });
            }
        }
    }

    [Fact]
    public async Task Working_surfaces_reflow_without_page_overflow()
    {
        using var assertions = new FluentAssertions.Execution.AssertionScope();
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await CheckAsync(page, new Uri(host.BaseAddress, "/ConfigureApp/Applications").ToString(), "applications");
        await CheckAsync(page, new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}").ToString(), "editor");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("analyst");
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await CheckAsync(page, new Uri(host.BaseAddress, "/Reports/").ToString(), "reports");

        await using var runtime = await BrowserTestFixture.StartAsync();
        await page.GotoAsync(new Uri(runtime.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await CheckAsync(page, page.Url, "runtime");
    }

    private static async Task CheckAsync(IPage page, string url, string surface)
    {
        await page.GotoAsync(url);
        foreach (var width in new[] { 1440, 375, 320 })
        {
            await page.SetViewportSizeAsync(width, 900);
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            var directory = Environment.GetEnvironmentVariable("QAB_UI_SCREENSHOTS");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"{surface}-{width}.png") });
            }
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1], $"{surface} must reflow at {width}px");
        }
    }

    private static async Task AssertFormFitsAsync(ILocator form, int viewportWidth)
    {
        await form.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        var bounds = await form.BoundingBoxAsync();
        bounds.Should().NotBeNull();
        bounds!.X.Should().BeGreaterThanOrEqualTo(0);
        (bounds.X + bounds.Width).Should().BeLessThanOrEqualTo(viewportWidth);
    }
}
