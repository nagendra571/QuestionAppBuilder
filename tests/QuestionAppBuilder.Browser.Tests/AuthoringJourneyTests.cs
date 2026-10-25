using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class AuthoringJourneyTests
{
    [Fact]
    public async Task Workspace_flushes_debounced_edit_before_opening_question_configuration()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue($"workspace navigation fits at {width}px");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-workspace-navigation-{width}.png"), FullPage = false });
        }
        await page.SetViewportSizeAsync(320, 800);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue("workspace navigation fits at 320px");
        await page.SetViewportSizeAsync(1280, 900);
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/ConfigureApp/Drafts/*/Save", async route =>
        {
            saveStarted.TrySetResult();
            await releaseSave.Task;
            await route.ContinueAsync();
        });
        await page.Locator("#draft-workspace").EvaluateAsync("form => form.dataset.saveDelay = '5000'");
        await page.Locator("[name='Version.DisplayName']").FillAsync("Saved before configuration navigation");
        var dirtyUnloadBlocked = await page.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        dirtyUnloadBlocked.Should().BeTrue();
        var modifiedClickPrevented = await page.GetByRole(AriaRole.Link, new() { Name = "Groups, rules & dynamic text", Exact = true })
            .EvaluateAsync<bool>("link => { const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ctrlKey: true }); link.dispatchEvent(event); return event.defaultPrevented; }");
        modifiedClickPrevented.Should().BeFalse("a Ctrl-click opens the destination separately and should not trigger a draft save");
        var configurationUrl = $"**/ConfigureApp/Composition/Index/{applicationId}";
        var navigation = page.WaitForURLAsync(configurationUrl, new PageWaitForURLOptions { Timeout = 15000 });
        try
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Groups, rules & dynamic text", Exact = true }).ClickAsync();
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.Url.Should().Contain($"/ConfigureApp/Drafts/{applicationId}", "navigation must wait for the pending draft save");
            await page.Locator("[name='Version.DisplayName']").FillAsync("Latest edit while save waits");
        }
        finally { releaseSave.TrySetResult(); }
        await navigation;
        await page.GetByRole(AriaRole.Heading, new() { Name = "Groups, rules & dynamic text" }).WaitForAsync();
        (await host.GetDraftDisplayNameAsync(applicationId)).Should().Be("Latest edit while save waits");
    }

    [Fact]
    public async Task Workspace_keeps_navigation_blocked_after_save_conflict_and_retries_with_edits_intact()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        await page.Locator("#draft-workspace").EvaluateAsync("form => form.dataset.saveDelay = '5000'");
        await page.RouteAsync("**/ConfigureApp/Drafts/*/Save", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = (int)HttpStatusCode.Conflict,
            ContentType = "application/json",
            Body = "{\"message\":\"The draft changed elsewhere. Your edits are still here.\"}"
        }));

        var displayName = page.Locator("[name='Version.DisplayName']");
        await displayName.FillAsync("Keep this edit after conflict");
        await page.GetByRole(AriaRole.Link, new() { Name = "Groups, rules & dynamic text", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "The draft changed elsewhere. Your edits are still here." }).WaitForAsync();
        page.Url.Should().Contain($"/ConfigureApp/Drafts/{applicationId}");
        (await displayName.InputValueAsync()).Should().Be("Keep this edit after conflict");
        var unloadBlocked = await page.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        unloadBlocked.Should().BeTrue();

        await page.UnrouteAsync("**/ConfigureApp/Drafts/*/Save");
        await page.GetByRole(AriaRole.Link, new() { Name = "Groups, rules & dynamic text", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"**/ConfigureApp/Composition/Index/{applicationId}");
        (await host.GetDraftDisplayNameAsync(applicationId)).Should().Be("Keep this edit after conflict");
    }

    [Fact]
    public async Task Conditional_content_visibility_is_reflected_in_designer_preview_review()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedContentVisibilityPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        await preview.Locator("[name='Answers[1].Text']").WaitForAsync();
        (await preview.Locator(".qab-content-panel").CountAsync()).Should().Be(0);
        var hiddenReport = preview.Locator("[data-hidden-preview-inspector]");
        await hiddenReport.WaitForAsync();
        await hiddenReport.Locator(":scope > summary").ClickAsync();
        var hiddenText = await hiddenReport.InnerTextAsync();
        hiddenText.Should().Contain("Conditional preview instructions").And.Contain("Visibility trigger is answered")
            .And.Contain("Dynamic preview instructions").And.Contain("dynamic value used by its label is unavailable");
        (await hiddenReport.Locator("a[href$='#visibility']").CountAsync()).Should().BeGreaterThan(0);
        (await hiddenReport.Locator("a[href$='#DynamicText']").CountAsync()).Should().Be(1);
        await page.SetViewportSizeAsync(1440, 1000);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-hidden-preview-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(375, 900);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-hidden-preview-mobile.png"), FullPage = true });
        await preview.Locator("[name='Answers[1].Text']").FillAsync("show the instructions");
        await preview.GetByText("Preview answers changed. Evaluate again to refresh preview explanations.", new() { Exact = true }).WaitForAsync();
        (await preview.Locator("[data-hidden-preview-inspector]").CountAsync()).Should().Be(0);
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await preview.Locator("[name='Answers[2].Text']").WaitForAsync();
        await preview.Locator("[name='Answers[2].Text']").FillAsync("complete the preview");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await preview.GetByRole(AriaRole.Heading, new() { Name = "Review your answers" }).WaitForAsync();
        (await preview.InnerTextAsync()).Should().Contain("Conditional preview instructions");
        await preview.Locator("[data-hidden-preview-inspector]").WaitForAsync();
        (await preview.Locator("[data-hidden-preview-inspector]").InnerTextAsync()).Should().Contain("Hidden questions and content (0)");
    }

    [Fact]
    public async Task Failed_combined_validation_preview_shows_actual_checks_and_opens_the_rule_without_saving()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var (applicationId, ruleId, combinedScoreRuleId) = await host.SeedCombinedValidationPreviewApplicationAsync();
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
        var inspector = preview.Locator("[data-preview-validation-inspector]");
        await inspector.WaitForAsync();
        await inspector.Locator(":scope > summary").ClickAsync();
        var report = await inspector.InnerTextAsync();
        report.Should().Contain("Failed combined validation rules (1)")
            .And.Contain("Minimum amount")
            .And.Contain("Increase the requested amount.")
            .And.Contain("Live feedback is disabled")
            .And.Contain("This rule requires all checks to pass.")
            .And.Contain("Passed: Requested amount used 0")
            .And.Contain("Failed: Requested amount used 0")
            .And.Contain("1 remaining check was not evaluated because an ALL check already failed.");
        var ruleLink = inspector.GetByRole(AriaRole.Link, new() { Name = "Edit this rule", Exact = true });
        (await ruleLink.GetAttributeAsync("href")).Should().EndWith($"#Rules/{ruleId:D}");
        var scoring = preview.Locator("[data-preview-scoring-inspector]");
        await scoring.WaitForAsync();
        await scoring.Locator(":scope > details > summary").ClickAsync();
        await scoring.GetByText("Combined scoring rules (2)", new() { Exact = true }).ClickAsync();
        await scoring.GetByText("First funding award", new() { Exact = true }).ClickAsync();
        await scoring.GetByText("Second funding award", new() { Exact = true }).ClickAsync();
        await scoring.GetByText("Required checks (all must pass)", new() { Exact = true }).First.ClickAsync();
        await scoring.GetByText("Required checks (all must pass)", new() { Exact = true }).Nth(1).ClickAsync();
        var scoreReport = await scoring.InnerTextAsync();
        scoreReport.Should().Contain("First funding award").And.Contain("Second funding award")
            .And.Contain("Failed: Requested amount is at least 10")
            .And.Contain("had no number, so it was read as 0");
        await page.SetViewportSizeAsync(1440, 1000);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-scoring-preview-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(375, 900);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-scoring-preview-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(1440, 1000);

        await preview.Locator("[name='Answers[1].Number']").FillAsync("50");
        (await preview.Locator("[data-preview-validation-inspector]").CountAsync()).Should().Be(0);
        (await preview.Locator("[data-hidden-preview-inspector]").CountAsync()).Should().Be(0);
        (await preview.Locator("[data-preview-scoring-inspector]").CountAsync()).Should().Be(0);
        (await preview.InnerTextAsync()).Should().Contain("Preview answers changed. Evaluate again to refresh preview explanations.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();
        await inspector.WaitForAsync();
        await inspector.Locator(":scope > summary").ClickAsync();
        (await inspector.InnerTextAsync()).Should().Contain("Passed: Requested amount used 50")
            .And.Contain("Failed: Requested amount used 50");
        var updatedScoring = preview.Locator("[data-preview-scoring-inspector]");
        await updatedScoring.Locator(":scope > details > summary").ClickAsync();
        await updatedScoring.GetByText("Combined scoring rules (2)", new() { Exact = true }).ClickAsync();
        await updatedScoring.GetByText("First funding award", new() { Exact = true }).ClickAsync();
        await updatedScoring.GetByText("Second funding award", new() { Exact = true }).ClickAsync();
        await updatedScoring.GetByText("Required checks (all must pass)", new() { Exact = true }).First.ClickAsync();
        await updatedScoring.GetByText("Required checks (all must pass)", new() { Exact = true }).Nth(1).ClickAsync();
        var updatedScoreReport = await updatedScoring.InnerTextAsync();
        updatedScoreReport.Should().Contain("The rule awarded 5 points.")
            .And.Contain("band was already awarded by First funding award");
        var combinedScoreLink = updatedScoring.GetByRole(AriaRole.Link, new() { Name = "Edit this combined rule", Exact = true }).First;
        (await combinedScoreLink.GetAttributeAsync("href")).Should().EndWith($"#Rules/{combinedScoreRuleId:D}");

        var saves = 0;
        await page.RouteAsync("**/ConfigureApp/Composition/Save/*", async route =>
        {
            saves++;
            await route.ContinueAsync();
        });
        var ruleNavigation = page.WaitForURLAsync($"**/ConfigureApp/Composition/Index/{applicationId}#Rules/{ruleId:D}");
        await ruleLink.ClickAsync();
        await ruleNavigation;
        var ruleCard = page.Locator($".composition-item[data-rule-id='{ruleId:D}']");
        await ruleCard.WaitForAsync();
        (await ruleCard.EvaluateAsync<bool>("card => card.open")).Should().BeTrue();
        (await ruleCard.EvaluateAsync<bool>("card => document.activeElement === card.querySelector(':scope > summary')")).Should().BeTrue();
        (await page.Locator("[data-composition-status]").InnerTextAsync()).Should().Be("All changes saved.");
        saves.Should().Be(0);

        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{applicationId}#Rules/11111111-1111-1111-1111-111111111111").ToString());
        (await page.Locator("[data-pane='Rules']").GetAttributeAsync("aria-pressed")).Should().Be("true");
        (await page.Locator("[data-composition-editor]").InnerTextAsync()).Should().Contain("Combined rules");
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{applicationId}#DynamicText").ToString());
        (await page.Locator("[data-composition-editor]").InnerTextAsync()).Should().Contain("Dynamic text");
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{applicationId}#not-a-pane").ToString());
        (await page.Locator("[data-composition-editor]").InnerTextAsync()).Should().Contain("Question groups");
    }

    [Fact]
    public async Task All_hidden_preview_renders_explanations_without_a_visible_category()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedAllHiddenPreviewApplicationAsync();
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

        var result = page.Locator("[data-preview-result]");
        await result.Locator(".qab-runtime-preview").WaitForAsync();
        await result.Locator("[data-hidden-preview-inspector]").WaitForAsync();
        (await result.Locator(".qab-category").CountAsync()).Should().Be(0);
        var report = result.Locator("[data-hidden-preview-inspector]");
        await report.Locator(":scope > summary").ClickAsync();
        (await report.InnerTextAsync()).Should().Contain("Hidden questions and content (2)")
            .And.Contain("Hidden question").And.Contain("Hidden content").And.Contain("Open visibility settings");
    }

    [Fact]
    public async Task Draft_edit_during_first_preview_request_discards_response_and_shows_recovery_message()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedContentVisibilityPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseStatus = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewResponse = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Preview/", StringComparison.Ordinal) && response.Url.EndsWith("/Evaluate", StringComparison.Ordinal));
        await page.RouteAsync("**/ConfigureApp/Preview/*/Evaluate", async route =>
        {
            var upstream = await route.FetchAsync();
            responseStatus.TrySetResult(upstream.Status);
            requestStarted.TrySetResult(true);
            await releaseResponse.Task;
            await route.FulfillAsync(new RouteFulfillOptions { Response = upstream });
        });
        var evaluateClick = page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            (await responseStatus.Task).Should().Be(200);
            await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.Locator("[name='Version.DisplayName']").FillAsync("Changed during preview request");
        }
        finally { releaseResponse.TrySetResult(true); }
        await evaluateClick;
        var response = await previewResponse.WaitAsync(TimeSpan.FromSeconds(20));
        (await response.FinishedAsync()).Should().BeNull();
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        var result = page.Locator("[data-preview-result]");
        (await result.InnerTextAsync()).Should().Contain("The draft changed while preview was loading. Save and evaluate again.");
        (await result.Locator(".qab-runtime-preview").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Custom_preview_without_trace_shows_explanations_are_unavailable()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedContentVisibilityPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        var revision = await page.Locator("#draft-workspace [name='ExpectedRevision']").InputValueAsync();
        var responseBody = JsonSerializer.Serialize(new
        {
            questionnaire = new { preview = true, categories = Array.Empty<object>() },
            score = new { total = 0, categories = Array.Empty<object>(), questions = Array.Empty<object>() },
            answerIssues = Array.Empty<object>(), definitionIssues = Array.Empty<object>(),
            html = "<main class=\"qab-runtime qab-runtime-preview\"><p>Custom preview.</p></main>",
            navigationMode = "Sequential", resultVisibility = "Total", categoryIndex = 0,
            isReceipt = false, revision, hiddenItems = (object?)null
        });
        await page.RouteAsync("**/ConfigureApp/Preview/*/Evaluate", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json", Body = responseBody
        }));

        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();

        var report = page.Locator("[data-hidden-preview-inspector]");
        await report.WaitForAsync();
        await report.Locator(":scope > summary").ClickAsync();
        (await report.InnerTextAsync()).Should().Contain("Hidden-item explanations")
            .And.Contain("unavailable from this preview evaluator");
        var validationReport = page.Locator("[data-preview-validation-inspector]");
        await validationReport.WaitForAsync();
        await validationReport.Locator(":scope > summary").ClickAsync();
        (await validationReport.InnerTextAsync()).Should().Contain("Combined-validation explanations")
            .And.Contain("unavailable for this evaluator or configuration");
        var scoringReport = page.Locator("[data-preview-scoring-inspector]");
        await scoringReport.WaitForAsync();
        await scoringReport.Locator(":scope > details > summary").ClickAsync();
        (await scoringReport.InnerTextAsync()).Should().Contain("Scoring explanations are unavailable from this evaluator.");
    }

    [Fact]
    public async Task Confirmation_and_answer_review_preview_shows_read_only_stacked_application()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Questionnaire settings" }).WaitForAsync();
        await page.Locator("[name='Version.ResultVisibility']").SelectOptionAsync("ConfirmationAndAnswerReview");
        var save = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.EndsWith("/Save"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
        (await save).Ok.Should().BeTrue();

        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        await preview.Locator("[data-preview-category='0'] [data-question-id]").First.WaitForAsync();
        var firstQuestionId = await preview.Locator("[data-preview-category='0'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        await preview.Locator($"[name='Answers[{firstQuestionId}].Number']").FillAsync("5");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await preview.Locator("[data-preview-category='1'] [data-question-id]").WaitForAsync();
        var secondQuestionId = await preview.Locator("[data-preview-category='1'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        await preview.Locator($"[name='Answers[{secondQuestionId}].Text']").FillAsync("Filled answer");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await preview.GetByRole(AriaRole.Heading, new() { Name = "Review your answers" }).WaitForAsync();
        await preview.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();
        var receiptView = preview.Locator("main.qab-runtime-preview");
        await receiptView.GetByRole(AriaRole.Heading, new() { Name = "Thank you" }).WaitForAsync();

        var applicationView = receiptView.Locator(".qab-submitted-application");
        var receipt = await applicationView.InnerTextAsync();
        receipt.Should().Contain("Filled answer").And.Contain("This is the score earned from the submitted answers.")
            .And.Contain("First category").And.Contain("Second category");
        (await applicationView.Locator("[data-preview-total]").CountAsync()).Should().Be(1);
        (await applicationView.Locator("[data-preview-total]").InnerTextAsync()).Should().Be("0");
        (await host.CountResponsesAsync()).Should().Be(0);
        (await host.CountSubmissionsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Preview_keeps_answers_across_sequential_categories_and_shows_runtime_receipt()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.Locator(".aw-outline-element").First.ClickAsync();
        await page.Locator(".aw-element:visible .aw-disclosure").First.Locator("summary").ClickAsync();
        var messageInput = page.Locator(".aw-element:visible").GetByLabel("Required-answer message (optional)", new() { Exact = true });
        await messageInput.FillAsync("Please enter the grant amount you are requesting.");
        var save = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.EndsWith("/Save"));
        await messageInput.BlurAsync();
        (await save).Ok.Should().BeTrue();
        await page.ReloadAsync();
        (await page.Locator(".aw-element:visible").GetByLabel("Required-answer message (optional)", new() { Exact = true }).InputValueAsync()).Should().Be("Please enter the grant amount you are requesting.");
        await page.Locator(".aw-element:visible .aw-disclosure").First.Locator("summary").ClickAsync();
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-required-message-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1440, 900);
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Questionnaire settings" }).WaitForAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        await preview.Locator("[data-preview-category='0'] [data-question-id]").First.WaitForAsync();
        var firstQuestionId = await preview.Locator("[data-preview-category='0'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        var number = preview.Locator($"[name='Answers[{firstQuestionId}].Number']");
        var blockedEvaluateTask = page.WaitForResponseAsync(response => response.Url.Contains("/Preview/", StringComparison.Ordinal)
            && response.Url.EndsWith("/Evaluate", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        var blockedEvaluate = await blockedEvaluateTask;
        var blockedBody = await blockedEvaluate.TextAsync();
        blockedEvaluate.Status.Should().Be(200, blockedBody);
        (await preview.InnerTextAsync()).Should().Contain("Complete the required fields before continuing.", blockedBody)
            .And.Contain("Please enter the grant amount you are requesting.");
        (await preview.Locator(".qab-runtime-preview").GetAttributeAsync("data-preview-category")).Should().Be("0");

        await number.FillAsync("5");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await preview.Locator("[data-preview-category='1'] [data-question-id]").WaitForAsync();
        var secondQuestionId = await preview.Locator("[data-preview-category='1'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        await preview.Locator($"[name='Answers[{secondQuestionId}].Text']").FillAsync("To serve my community");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await preview.GetByRole(AriaRole.Heading, new() { Name = "Review your answers" }).WaitForAsync();
        var reviewText = await preview.InnerTextAsync();
        reviewText.Should().Contain("5").And.Contain("To serve my community");

        await preview.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();
        await preview.GetByRole(AriaRole.Heading, new() { Name = "Thank you" }).WaitForAsync();
        (await preview.InnerTextAsync()).Should().Contain("Total score: 0")
            .And.Contain("This is a preview. No response or submission was created.");
        (await host.CountResponsesAsync()).Should().Be(0);
        (await host.CountSubmissionsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Designer_can_create_and_edit_a_draft_and_keep_local_input_after_conflict()
    {
        await using var host = await SampleHostFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, message) => pageErrors.Add(message);
        page.SetDefaultNavigationTimeout(90_000);

        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Applications" }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "New application" }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Browser-authored " + Guid.NewGuid().ToString("N"));
        var type = await page.Locator("select[name='ApplicationTypeId'] option").First.GetAttributeAsync("value");
        type.Should().NotBeNullOrWhiteSpace("database migrations seed an application classification");
        await page.Locator("select[name='ApplicationTypeId']").SelectOptionAsync(type!);
        var createResponseTask = page.WaitForResponseAsync(response =>
            response.Request.Method == "POST" && response.Url.Contains("/ConfigureApp/Applications", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 90_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Create application" }).ClickAsync();
        IResponse createResponse;
        try { createResponse = await createResponseTask; }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Create application POST did not receive a response. SampleHost logs:{Environment.NewLine}{host.GetDiagnosticLogs()}", exception);
        }
        if (createResponse.Status >= 500)
            throw new InvalidOperationException($"Create application returned HTTP {createResponse.Status}: {await createResponse.TextAsync()}{Environment.NewLine}{host.GetDiagnosticLogs()}");
        await page.WaitForURLAsync("**/ConfigureApp/Applications/**", new() { Timeout = 90_000 });

        await page.GetByRole(AriaRole.Link, new() { Name = "Open authoring workspace" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Questionnaire settings" }).WaitForAsync();
        var cssStatus = await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Editor/css/editor.css')).status");
        var jsStatus = await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Editor/js/editor.js')).status");
        cssStatus.Should().Be((int)HttpStatusCode.OK);
        jsStatus.Should().Be((int)HttpStatusCode.OK);
        var editorScript = await page.EvaluateAsync<string>("async () => await (await fetch('/_content/QuestionAppBuilder.Editor/js/editor.js', { cache: 'no-store' })).text()");
        editorScript.Should().Contain("const saveCurrentDraft", "the isolated host must serve the implementation under test");

        await page.Locator(".aw-outline-action[href='#add-category']").ClickAsync();
        await page.Locator("[name='categoryName']").FillAsync("Applicant details");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add category" }).ClickAsync();
        (await page.Locator("[name='Categories[0].Name']").InputValueAsync()).Should().Be("Applicant details");
        await page.Locator(".aw-outline-action[href='#add-element']").ClickAsync();
        await page.Locator("[name='elementLabel']").FillAsync("Application method");
        await page.Locator("[name='elementType']").SelectOptionAsync("RadioGroup");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add element" }).ClickAsync();
        (await page.Locator("[name='Elements[0].Label']").InputValueAsync()).Should().Be("Application method");
        await page.Locator(".aw-outline-action[href='#question-assets']").ClickAsync();
        await page.Locator("[name='optionLabel']").FillAsync("Online");
        await page.Locator("[name='optionValue']").FillAsync("online");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add option" }).ClickAsync();
        (await page.Locator("[name='Options[0].Label']").InputValueAsync()).Should().Be("Online");
        await page.Locator(".aw-outline-element").Filter(new() { HasText = "Application method" }).ClickAsync();
        var duplicateButton = page.GetByRole(AriaRole.Button, new() { Name = "Duplicate element" });
        var duplicateResponseTask = page.WaitForResponseAsync(response => response.Url.Contains("/Structure/DuplicateElement", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        var duplicateWorkspaceTask = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await duplicateButton.ClickAsync();
        (await duplicateResponseTask).Status.Should().Be((int)HttpStatusCode.OK);
        (await duplicateWorkspaceTask).Status.Should().Be((int)HttpStatusCode.OK);
        await page.Locator(".aw-outline-element").Filter(new() { HasText = "Application method copy" }).ClickAsync();
        await page.Locator("[name='Elements[1].Label']").WaitForAsync();
        (await page.Locator("[name='Elements[1].Label']").InputValueAsync()).Should().Be("Application method copy");
        var moveResponseTask = page.WaitForResponseAsync(response => response.Url.Contains("/Structure/MoveElement", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        var movedWorkspaceTask = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Move Application method copy up" }).ClickAsync();
        (await moveResponseTask).Status.Should().Be((int)HttpStatusCode.OK);
        (await movedWorkspaceTask).Status.Should().Be((int)HttpStatusCode.OK);
        await page.Locator("[name='Elements[0].Label']").WaitForAsync();
        (await page.Locator("[name='Elements[0].Label']").InputValueAsync()).Should().Be("Application method copy");

        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        var displayName = page.Locator("[name='Version.DisplayName']");
        var savedRequest = page.WaitForResponseAsync(response =>
            response.Url.Contains("/Drafts/", StringComparison.Ordinal) && response.Url.EndsWith("/Save", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await displayName.FillAsync("Saved from a real browser");
        var saveResponse = await savedRequest;
        if (saveResponse.Status != (int)HttpStatusCode.OK)
            throw new InvalidOperationException($"Draft autosave returned {saveResponse.Status}: {await saveResponse.TextAsync()}");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        (await displayName.InputValueAsync()).Should().Be("Saved from a real browser");

        await page.Locator(".aw-outline-action[href='#add-element']").ClickAsync();
        await page.Locator("[name='elementLabel']").FillAsync("Requested amount");
        await page.Locator("[name='elementType']").SelectOptionAsync("Number");
        var addNumberResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddElement", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Add element" }).ClickAsync();
        var addNumberResponse = await addNumberResponseTask;
        addNumberResponse.Status.Should().Be((int)HttpStatusCode.Redirect);
        await page.GetByText("Requested amount").First.WaitForAsync();
        var numericQuestionId = await page.Locator("#structure-question option").Last.GetAttributeAsync("value");
        numericQuestionId.Should().NotBeNullOrWhiteSpace();
        await page.Locator(".aw-outline-action[href='#question-assets']").ClickAsync();
        await page.Locator("#structure-question").SelectOptionAsync(numericQuestionId!);
        await page.Locator("select[name='family']").SelectOptionAsync("NumericRange");
        await page.Locator("input[name='points']").FillAsync("1");
        await page.Locator("input[name='minValue']").FillAsync("0");
        await page.Locator("input[name='maxValue']").FillAsync("10");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        var addRuleRequests = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Structure/AddRule", StringComparison.Ordinal))
                addRuleRequests.Add(request.Url);
        };
        var firstRuleResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddRule", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Add scoring rule" }).ClickAsync();
        IResponse firstRuleResponse;
        try { firstRuleResponse = await firstRuleResponseTask; }
        catch (TimeoutException exception)
        {
            var state = await page.EvaluateAsync<string>("() => JSON.stringify({ url: location.href, active: document.activeElement?.outerHTML, status: document.querySelector('[data-save-status]')?.textContent, readiness: document.querySelector('[data-readiness-status]')?.textContent, ruleButton: [...document.querySelectorAll('button')].find(button => button.textContent.includes('Add scoring rule'))?.outerHTML, form: document.querySelector('#draft-workspace')?.action, invalid: [...(document.querySelector('#draft-workspace')?.querySelectorAll(':invalid') || [])].map(input => ({ name: input.name, message: input.validationMessage })) })");
            throw new TimeoutException($"AddRule did not receive a response. Browser state: {state}; page errors: {string.Join(" | ", pageErrors)}{Environment.NewLine}{host.GetDiagnosticLogs()}", exception);
        }
        firstRuleResponse.Status.Should().Be((int)HttpStatusCode.Redirect);
        addRuleRequests.Should().ContainSingle("one click on Add scoring rule must submit exactly one structure request");
        await page.GetByText("No scoring rules are defined.").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Locator(".aw-outline-action[href='#question-assets']").ClickAsync();
        await page.Locator("#structure-question").SelectOptionAsync(numericQuestionId!);
        await page.Locator("select[name='family']").SelectOptionAsync("NumericRange");
        await page.Locator("input[name='points']").FillAsync("2");
        await page.Locator("input[name='minValue']").FillAsync("5");
        await page.Locator("input[name='maxValue']").FillAsync("15");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        var secondRuleResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddRule", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Add scoring rule" }).ClickAsync();
        (await secondRuleResponseTask).Status.Should().Be((int)HttpStatusCode.Redirect);
        await page.WaitForTimeoutAsync(300);
        await page.GetByRole(AriaRole.Link, new() { Name = "Review & publish", Exact = true }).ClickAsync();
        await page.Locator("#readiness:visible").WaitForAsync();
        var afterRules = await page.Locator("body").InnerTextAsync();
        afterRules.Should().Contain("Numeric rules overlap; only the first matching rule contributes.", $"workspace after adding overlapping rules: {afterRules}");
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        var previewQuestion = page.Locator($"[data-preview-result] [data-question-id='{numericQuestionId}']");
        await previewQuestion.WaitForAsync();
        (await previewQuestion.InnerTextAsync()).Should().Contain("Requested amount");
        await previewQuestion.Locator($"[name='Answers[{numericQuestionId}].Number']").FillAsync("7");
        await page.GetByRole(AriaRole.Button, new() { Name = "Update preview and scoring" }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        await preview.GetByText("Total score: 1", new() { Exact = true }).WaitForAsync();
        string.Join(" ", await preview.Locator(".aw-preview-score details p").AllTextContentsAsync())
            .Should().Contain("Rule 1 · Numeric Range").And.Contain("contributed 1 point").And.Contain("first matching rule");
        await page.GetByRole(AriaRole.Link, new() { Name = "Review & publish", Exact = true }).ClickAsync();
        var acknowledgments = page.Locator("#publish-draft input[name='AcknowledgedWarningCodes']");
        await acknowledgments.First.CheckAsync();
        (await acknowledgments.First.IsCheckedAsync()).Should().BeTrue();

        var refreshedReadiness = page.WaitForResponseAsync(response => response.Url.Contains("/Drafts/", StringComparison.Ordinal)
            && response.Url.Contains("/Readiness", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        var updatedName = page.Locator("[name='Version.DisplayName']");
        await updatedName.FillAsync("Changed after acknowledging warnings");
        var readinessResponse = await refreshedReadiness;
        readinessResponse.Status.Should().Be((int)HttpStatusCode.OK);
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        (await acknowledgments.CountAsync()).Should().Be(1);
        (await acknowledgments.First.IsCheckedAsync()).Should().BeFalse("a warning acknowledgment applies only to the revision that was reviewed");
        (await page.Locator("[data-readiness-status]").InnerTextAsync()).Should().Contain("Readiness checked against the saved revision");

        await page.RouteAsync("**/ConfigureApp/Drafts/*/Save", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = (int)HttpStatusCode.Conflict,
            ContentType = "application/json",
            Body = "{\"message\":\"The draft changed elsewhere. Your edits are still here.\"}"
        }));
        await page.Locator("#draft-workspace").EvaluateAsync("form => form.dataset.saveDelay = '10'");
        var conflictRequest = page.WaitForResponseAsync(response => response.Url.EndsWith("/Save", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 5000 });
        await displayName.FillAsync("Retain this value after a conflict");
        var conflictResponse = await conflictRequest;
        conflictResponse.Status.Should().Be((int)HttpStatusCode.Conflict);
        await page.WaitForTimeoutAsync(100);
        (await page.Locator("[data-save-status]").InnerTextAsync()).Should().Contain("The draft changed elsewhere");
        (await displayName.InputValueAsync()).Should().Be("Retain this value after a conflict");
        await context.CloseAsync();
    }

}
