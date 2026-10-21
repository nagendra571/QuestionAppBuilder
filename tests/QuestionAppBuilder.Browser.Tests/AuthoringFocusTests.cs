using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class AuthoringFocusTests
{
    [Fact]
    public async Task Expandable_forms_close_without_submitting_and_keep_input_and_keyboard_focus()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedResponsiveAuthoringApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Applications/Detail/{id}").ToString());
        var posts = new List<string>();
        page.Request += (_, request) => { if (request.Method == "POST") posts.Add(request.Url); };
        var panels = page.Locator(".cv-rename, .cv-add-option, .cv-add-rule, .cv-add-form, .cv-publish-panel");
        (await panels.CountAsync()).Should().Be(6);
        for (var index = 0; index < await panels.CountAsync(); index++)
        {
            var panel = panels.Nth(index);
            var opener = panel.Locator(":scope > summary");
            await opener.ClickAsync();
            var input = panel.Locator("input[name='Label']");
            if (await input.CountAsync() > 0) await input.FillAsync("Keep this unsaved text");
            var close = panel.Locator("[data-close-panel]");
            (await close.GetAttributeAsync("type")).Should().Be("button");
            await close.ClickAsync();
            (await panel.GetAttributeAsync("open")).Should().BeNull();
            (await opener.EvaluateAsync<bool>("element => document.activeElement === element")).Should().BeTrue();
            await opener.ClickAsync();
            if (await input.CountAsync() > 0) (await input.InputValueAsync()).Should().Be("Keep this unsaved text");
            await close.ClickAsync();
        }
        posts.Should().BeEmpty("closing a panel must never submit its form");
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}#add-element").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Close question or content form", Exact = true }).ClickAsync();
        await page.Locator("#content:visible").WaitForAsync();
    }

    [Fact]
    public async Task Application_management_keeps_categories_above_fold_and_management_controls_accessible()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedHostSignInApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Applications/Detail/{id}").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start editing", Exact = true }).ClickAsync();
        await page.Locator("#application-publishing").WaitForAsync();
        (await page.Locator("#respondent-availability").GetAttributeAsync("open")).Should().BeNull();
        foreach (var width in new[] { 1440, 375, 320 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.EvaluateAsync("scrollTo(0, 0)");
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1]);
            if (width == 1440)
            {
                (await page.Locator(".cv-category-nav").BoundingBoxAsync())!.Y.Should().BeLessThan(340);
                (await page.Locator(".cv-q").First.BoundingBoxAsync())!.Y.Should().BeLessThan(560);
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-detail-compact-{width}.png"), FullPage = true });
        }
        await page.Locator("#respondent-availability > summary").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save availability", Exact = true }).WaitForAsync();
        await page.Locator("#application-publishing > summary").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Publish version 2", Exact = true }).WaitForAsync();
    }

    [Fact]
    public async Task Workspace_focuses_one_question_and_preserves_context_after_structure_changes()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{id}").ToString());
        await page.Locator("[data-workspace-nav]").WaitForAsync();
        (await page.Locator("[data-workspace-pane]:visible").CountAsync()).Should().Be(1);
        var last = page.Locator(".aw-outline-element").Last;
        var target = await last.GetAttributeAsync("href");
        await last.ClickAsync();
        await page.Locator($"{target}:visible").WaitForAsync();
        (await page.Locator(".aw-element:visible").CountAsync()).Should().Be(1);
        (await page.Locator(".aw-category:visible").CountAsync()).Should().Be(1);
        foreach (var width in new[] { 1440, 375, 320 })
        {
            await page.SetViewportSizeAsync(width, 900);
            var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, innerWidth]");
            dimensions[0].Should().BeLessThanOrEqualTo(dimensions[1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-authoring-workspace-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1440, 900);
        await page.Locator($"{target} [name$='.Label']").FillAsync("Edited without removing");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync();
        var reloaded = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/"));
        await page.Locator($"{target} [data-structure-action*='DuplicateElement']").ClickAsync();
        await reloaded;
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.Locator($"{target}:visible").WaitForAsync();
        (await page.Locator($"{target} [name$='.Label']").InputValueAsync()).Should().Be("Edited without removing");
        await page.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        await page.Locator(".qab-runtime-preview").WaitForAsync();
        (await page.Locator(".aw-category:visible").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Option_edit_keeps_scoring_links_and_repeated_additions_keep_the_form_open()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var id = await host.SeedResponsiveAuthoringApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        var detail = new Uri(host.BaseAddress, $"/ConfigureApp/Applications/Detail/{id}").ToString();
        await page.GotoAsync(detail);
        var optionActions = page.Locator(".cv-opt").First.Locator(".cv-row-actions");
        (await optionActions.Locator(".qab-icon-action").CountAsync()).Should().Be(2);
        (await optionActions.InnerTextAsync()).Should().BeNullOrWhiteSpace("repeated actions should use icons with accessible names");
        var removeIcon = optionActions.GetByRole(AriaRole.Button, new() { Name = "Remove option:", Exact = false });
        await removeIcon.FocusAsync();
        (await removeIcon.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle")).Should().NotBe("none");
        var edit = page.Locator(".cv-opt-edit").First;
        var optionTarget = new Uri(new Uri(detail), (await edit.GetAttributeAsync("href"))!).Fragment;
        await edit.ClickAsync();
        await page.Locator($"{optionTarget}:visible").WaitForAsync();
        var label = page.Locator($"{optionTarget} [name$='.Label']");
        await label.FillAsync("Updated option, same scoring reference");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync();
        await page.GotoAsync(detail);
        (await page.Locator(".cv-rule").InnerTextAsync()).Should().Contain("Updated option, same scoring reference");
        var add = page.Locator(".cv-add-option");
        await add.Locator("summary").ClickAsync();
        for (var index = 0; index < 2; index++)
        {
            await add.Locator("[name='Label']").FillAsync($"New option {index}");
            await add.Locator("[name='Value']").FillAsync($"new-{index}");
            var addButton = add.GetByRole(AriaRole.Button, new() { Name = "Add option", Exact = true });
            await addButton.ScrollIntoViewIfNeededAsync();
            var position = await page.EvaluateAsync<double>("scrollY");
            await addButton.ClickAsync();
            await page.Locator(".cv-opt").Filter(new() { HasText = $"New option {index}" }).WaitForAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            (await add.GetAttributeAsync("open")).Should().NotBeNull();
            Math.Abs(await page.EvaluateAsync<double>("scrollY") - position).Should().BeLessThan(100);
        }
        (await page.Locator(".cv-opt").CountAsync()).Should().Be(4);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-authoring-detail-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(375, 900);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-authoring-detail-mobile.png"), FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove option: New option 1", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove option: New option 1", Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await page.Locator(".cv-opt").CountAsync()).Should().Be(3);
    }
}
