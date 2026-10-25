using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class WorkspaceSearchJourneyTests
{
    [Fact]
    public async Task Search_flushes_draft_reports_direct_references_and_opens_exact_targets()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDependencyInspectorApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());

        var compositionSavePosts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Composition/Save", StringComparison.Ordinal))
                Interlocked.Increment(ref compositionSavePosts);
        };
        var categoryName = page.Locator("[name='Categories[0].Name']");
        await categoryName.FillAsync("Applicant cohort");
        var save = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.EndsWith("/Save", StringComparison.Ordinal));
        var searchInput = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this draft" });
        await searchInput.FillAsync("Applicant cohort");
        await searchInput.PressAsync("Enter");
        (await save).Status.Should().Be(200, "search flushes the debounced draft edit before it reads the saved definition");
        var results = page.Locator("[data-search-results]");
        await results.GetByRole(AriaRole.Link, new() { Name = "Category: Applicant cohort", Exact = true }).WaitForAsync();
        (await results.GetAttributeAsync("data-stale")).Should().Be("false");

        async Task Search(string query)
        {
            await searchInput.FillAsync(query);
            var response = page.WaitForResponseAsync(result => result.Request.Method == "GET"
                && result.Url.Contains($"/Drafts/{applicationId}/Search", StringComparison.Ordinal));
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
            (await response).Status.Should().Be(200);
            await page.Locator("[data-search-status]").GetByText("Search complete.", new() { Exact = true }).WaitForAsync();
        }

        async Task ReturnToWorkspace()
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Questionnaire", Exact = true }).ClickAsync();
            await page.Locator("[data-workspace-search]").WaitForAsync();
            results = page.Locator("[data-search-results]");
            searchInput = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this draft" });
        }

        await Search("Annual income");
        var matchedGroupRow = results.GetByRole(AriaRole.Listitem).Filter(new() { HasText = "Group: Applicant data" });
        await matchedGroupRow.GetByText("Matches reference: Annual income", new() { Exact = false }).WaitForAsync();
        var groupLink = results.GetByRole(AriaRole.Link, new() { Name = "Group: Applicant data", Exact = true });
        var groupNavigation = page.WaitForURLAsync("**/Composition/Index/**");
        await groupLink.ClickAsync();
        await groupNavigation;
        new Uri(page.Url).Fragment.Should().MatchRegex("^#Groups/[0-9a-f-]{36}$");
        var groupCard = page.Locator(".composition-item[data-group-id]").First;
        (await groupCard.EvaluateAsync<bool>("card => card.open")).Should().BeTrue();
        (await groupCard.EvaluateAsync<bool>("card => card.querySelector(':scope > summary') === document.activeElement")).Should().BeTrue();
        compositionSavePosts.Should().Be(0, "search result navigation only opens saved configuration and must not write composition");

        await ReturnToWorkspace();
        await Search("Income subtotal");
        var calculationNavigation = page.WaitForURLAsync("**/Composition/Index/**");
        await results.GetByRole(AriaRole.Link, new() { Name = "Calculation: Income subtotal", Exact = true }).ClickAsync();
        await calculationNavigation;
        new Uri(page.Url).Fragment.Should().MatchRegex("^#Calculations/[0-9a-f-]{36}$");
        var calculationCard = page.Locator(".composition-item[data-calculation-id]").First;
        (await calculationCard.EvaluateAsync<bool>("card => card.open")).Should().BeTrue();
        (await calculationCard.EvaluateAsync<bool>("card => card.querySelector(':scope > summary') === document.activeElement")).Should().BeTrue();
        compositionSavePosts.Should().Be(0);

        await ReturnToWorkspace();
        await Search("Income display");
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue($"search fits at {width}px");
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-workspace-search-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1280, 900);
        var variableNavigation = page.WaitForURLAsync("**/Composition/Index/**");
        await results.GetByRole(AriaRole.Link, new() { Name = "Dynamic value: Income display", Exact = true }).ClickAsync();
        await variableNavigation;
        new Uri(page.Url).Fragment.Should().MatchRegex("^#DynamicText/Variables/[0-9a-f-]{36}$");
        var variableCard = page.Locator(".composition-dynamic-item[data-dynamic-variable-id]").First;
        (await variableCard.EvaluateAsync<bool>("card => card.open")).Should().BeTrue();
        (await variableCard.EvaluateAsync<bool>("card => card.querySelector(':scope > summary') === document.activeElement")).Should().BeTrue();
        compositionSavePosts.Should().Be(0);

        await ReturnToWorkspace();
        await Search("Income note");
        var templateNavigation = page.WaitForURLAsync("**/Composition/Index/**");
        await results.GetByRole(AriaRole.Link, new() { Name = "Text template: Dynamic label · Income note", Exact = true }).ClickAsync();
        await templateNavigation;
        new Uri(page.Url).Fragment.Should().MatchRegex("^#DynamicText/Templates/ElementLabel/[0-9a-f-]{36}$");
        var templateCard = page.Locator(".composition-dynamic-item[data-dynamic-template-slot]").First;
        (await templateCard.EvaluateAsync<bool>("card => card.open")).Should().BeTrue();
        (await templateCard.EvaluateAsync<bool>("card => card.querySelector(':scope > summary') === document.activeElement")).Should().BeTrue();
        compositionSavePosts.Should().Be(0);

        await ReturnToWorkspace();
        await Search("Annual income");
        var questionLink = results.GetByRole(AriaRole.Link, new() { Name = "Question: Annual income", Exact = true });
        await questionLink.ClickAsync();
        new Uri(page.Url).Fragment.Should().MatchRegex("^#element-[1-9][0-9]*$");
        var activeTarget = page.Locator(new Uri(page.Url).Fragment);
        await activeTarget.WaitForAsync();
        (await activeTarget.EvaluateAsync<bool>("target => target === document.activeElement")).Should().BeTrue("opening a result moves keyboard focus to the exact editor target");

        await Search("Answered");
        var conditionLink = results.GetByRole(AriaRole.Link, new() { Name = "Visibility: Visibility condition", Exact = true });
        await conditionLink.ClickAsync();
        new Uri(page.Url).Fragment.Should().MatchRegex("^#condition-[1-9][0-9]*$");
        var conditionTarget = page.Locator(new Uri(page.Url).Fragment);
        await conditionTarget.WaitForAsync();
        (await conditionTarget.EvaluateAsync<bool>("target => target === document.activeElement")).Should().BeTrue("visibility results open and focus their condition");
        compositionSavePosts.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_discards_a_successful_old_request_after_query_or_draft_change(bool editDraft)
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDependencyInspectorApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamStatus = 0;
        await page.RouteAsync("**/Drafts/*/Search**", async route =>
        {
            var upstream = await route.FetchAsync();
            upstreamStatus = upstream.Status;
            started.TrySetResult();
            await release.Task;
            await route.FulfillAsync(new RouteFulfillOptions { Response = upstream });
        });
        var responseTask = page.WaitForResponseAsync(response => response.Request.Method == "GET"
            && response.Url.Contains($"/Drafts/{applicationId}/Search", StringComparison.Ordinal));
        var searchInput = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search this draft" });
        await searchInput.FillAsync("Annual income");
        var submit = searchInput.PressAsync("Enter");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            upstreamStatus.Should().Be(200, "the held response was a successful result for the old saved revision");
            if (editDraft)
            {
                await page.Locator("[name='Categories[0].Name']").FillAsync("Updated Applicant");
                await page.Locator("[data-search-results]").GetByText("The draft changed.", new() { Exact = false }).WaitForAsync();
            }
            else
            {
                await searchInput.FillAsync("Income subtotal");
                await page.Locator("[data-search-results]").GetByText("Search text changed.", new() { Exact = false }).WaitForAsync();
            }
        }
        finally { release.TrySetResult(); }
        await submit;
        var response = await responseTask;
        response.Status.Should().Be(200);
        (await response.FinishedAsync()).Should().BeNull();
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        var results = page.Locator("[data-search-results]");
        (await results.GetAttributeAsync("data-stale")).Should().Be("true");
        var statusMessage = editDraft ? "The draft changed." : "Search text changed.";
        (await results.InnerTextAsync()).Should().Contain(statusMessage).And.NotContain("Question: Annual income");

        await searchInput.FillAsync(editDraft ? "Updated Applicant" : "Income subtotal");
        var refreshed = page.WaitForResponseAsync(candidate => candidate.Request.Method == "GET"
            && candidate.Url.Contains($"/Drafts/{applicationId}/Search", StringComparison.Ordinal));
        await searchInput.PressAsync("Enter");
        (await refreshed).Status.Should().Be(200);
        await page.Locator("[data-search-results]").GetByRole(AriaRole.Link,
            new() { Name = editDraft ? "Category: Updated Applicant" : "Calculation: Income subtotal", Exact = true }).WaitForAsync();
    }
}
