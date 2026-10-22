using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public class CompositionJourneyTests
{
    [Fact]
    public async Task Designer_builds_budget_group_calculation_and_validation_then_publishes_and_submits()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var appId = await host.SeedCompositionApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var designer = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var errors = new List<string>();
        designer.PageError += (_, message) => errors.Add(message);
        await Login(designer, host.BaseAddress, "designer");

        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{appId}").ToString());
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add group", Exact = true }).ClickAsync();
        var focusProbe = designer.Locator(".composition-item").Last;
        await focusProbe.GetByLabel("Name", new() { Exact = true }).FillAsync("Focus probe");
        await focusProbe.GetByLabel("Question to add", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        (await designer.EvaluateAsync<string>("document.activeElement.getAttribute('aria-label')"))
            .Should().Be("Remove Requested amount", "adding a row to a zero-column group should preserve keyboard focus on its row action");
        await designer.EvaluateAsync("window.confirm = () => true");
        await focusProbe.GetByRole(AriaRole.Button, new() { Name = "Delete group", Exact = true }).ClickAsync();

        await designer.GetByRole(AriaRole.Button, new() { Name = "Add group", Exact = true }).ClickAsync();
        var group = designer.Locator(".composition-item").Last;
        await group.GetByLabel("Name", new() { Exact = true }).FillAsync("Funding sources");
        await group.GetByLabel("Information column headings (separate with |)", new() { Exact = true })
            .FillAsync("Source | Purpose");
        await group.GetByLabel("Question to add", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        await group.GetByLabel("Question to add", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Matching amount" });
        var rows = group.Locator(".composition-group-row");
        await rows.Nth(0).GetByLabel("Source for Requested amount", new() { Exact = true }).FillAsync("Requested funds");
        await rows.Nth(0).GetByLabel("Purpose for Requested amount", new() { Exact = true }).FillAsync("Project costs");
        await rows.Nth(1).GetByLabel("Source for Matching amount", new() { Exact = true }).FillAsync("Matching funds");
        await rows.Nth(1).GetByLabel("Purpose for Matching amount", new() { Exact = true }).FillAsync("Other support");
        (await group.InnerTextAsync()).Should().Contain("Requested amount").And.Contain("Matching amount");

        await designer.GetByRole(AriaRole.Button, new() { Name = "Calculated fields", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add calculated field", Exact = true }).ClickAsync();
        var calculation = designer.Locator(".composition-item").Last;
        await calculation.GetByLabel("Name", new() { Exact = true }).FillAsync("Total budget");
        await calculation.GetByRole(AriaRole.Button, new() { Name = "Add source", Exact = true }).ClickAsync();
        await calculation.GetByRole(AriaRole.Button, new() { Name = "Add source", Exact = true }).ClickAsync();
        await calculation.GetByLabel("Source 1", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        await calculation.GetByLabel("Source 2", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Matching amount" });

        await designer.GetByRole(AriaRole.Button, new() { Name = "Combined rules", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add rule", Exact = true }).ClickAsync();
        var rule = designer.Locator(".composition-item").Last;
        await rule.GetByLabel("Name", new() { Exact = true }).FillAsync("Budget ceiling");
        await rule.GetByLabel("Validation message", new() { Exact = true }).FillAsync("The total budget must not exceed $1,000.");
        var checks = rule.Locator("fieldset").Last;
        await checks.GetByRole(AriaRole.Button, new() { Name = "Add check", Exact = true }).ClickAsync();
        var check = rule.Locator("fieldset").Last;
        await check.GetByLabel("Value", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Calculated: Total budget" });
        await check.GetByLabel("Comparison", new() { Exact = true }).SelectOptionAsync("LessThanOrEqual");
        await check.GetByLabel("Number", new() { Exact = true }).FillAsync("1000");
        var showWhileAnswering = rule.GetByLabel("Show validation while answering", new() { Exact = true });
        (await showWhileAnswering.IsCheckedAsync()).Should().BeFalse("live validation is opt-in for existing and newly authored rules");
        await showWhileAnswering.CheckAsync();

        var save = designer.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.Contains("/Composition/Save", StringComparison.Ordinal));
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        var savedPayload = await (await save).JsonAsync();
        savedPayload!.Value.GetProperty("message").GetString().Should().Contain("saved");
        await designer.GetByText("Configuration saved.", new() { Exact = true }).WaitForAsync();
        await designer.ReloadAsync();
        var savedGroup = designer.Locator(".composition-item").Last;
        var savedRows = savedGroup.Locator(".composition-group-row");
        (await savedRows.Nth(0).GetByLabel("Source for Requested amount", new() { Exact = true }).InputValueAsync()).Should().Be("Requested funds");
        (await savedRows.Nth(0).GetByLabel("Purpose for Requested amount", new() { Exact = true }).InputValueAsync()).Should().Be("Project costs");
        (await savedRows.Nth(1).GetByLabel("Source for Matching amount", new() { Exact = true }).InputValueAsync()).Should().Be("Matching funds");
        (await savedRows.Nth(1).GetByLabel("Purpose for Matching amount", new() { Exact = true }).InputValueAsync()).Should().Be("Other support");
        await CapturePane(designer, "groups", densityBound: true);

        await designer.GetByRole(AriaRole.Button, new() { Name = "Calculated fields", Exact = true }).ClickAsync();
        var savedCalculation = designer.Locator(".composition-item").Last;
        (await savedCalculation.GetByLabel("Name", new() { Exact = true }).InputValueAsync()).Should().Be("Total budget");
        (await savedCalculation.GetByLabel("Source 1", new() { Exact = true }).Locator("option:checked").InnerTextAsync()).Should().Be("Requested amount");
        (await savedCalculation.GetByLabel("Source 2", new() { Exact = true }).Locator("option:checked").InnerTextAsync()).Should().Be("Matching amount");
        await CapturePane(designer, "calculations");

        await designer.GetByRole(AriaRole.Button, new() { Name = "Combined rules", Exact = true }).ClickAsync();
        var savedRule = designer.Locator(".composition-item").Last;
        (await savedRule.GetByLabel("Validation message", new() { Exact = true }).InputValueAsync()).Should().Be("The total budget must not exceed $1,000.");
        (await savedRule.GetByLabel("Show validation while answering", new() { Exact = true }).IsCheckedAsync()).Should().BeTrue();
        var savedCheck = savedRule.Locator("fieldset").Last;
        (await savedCheck.GetByLabel("Value", new() { Exact = true }).Locator("option:checked").InnerTextAsync()).Should().Be("Calculated: Total budget");
        (await savedCheck.GetByLabel("Number", new() { Exact = true }).InputValueAsync()).Should().Be("1000");
        await CapturePane(designer, "rules");
        await designer.SetViewportSizeAsync(820, 900);
        (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue("the rules pane should fit at tablet width");

        await designer.GetByRole(AriaRole.Button, new() { Name = "Dynamic text", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add value", Exact = true }).ClickAsync();
        var dynamicValue = designer.Locator(".composition-dynamic-item").First;
        await dynamicValue.GetByLabel("Name", new() { Exact = true }).FillAsync("Requested total");
        await dynamicValue.GetByLabel("Source 1", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Requested amount (Number)" });
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add text template", Exact = true }).ClickAsync();
        var dynamicTemplate = designer.Locator(".composition-dynamic-item").Last;
        await dynamicTemplate.GetByLabel("Text location", new() { Exact = true }).SelectOptionAsync("ReviewSummary");
        await dynamicTemplate.GetByLabel("Part 1", new() { Exact = true }).SelectOptionAsync("Value");
        await dynamicTemplate.GetByLabel("Value to insert 1", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Value: Requested total" });
        await dynamicTemplate.GetByLabel("Hide when unavailable", new() { Exact = true }).CheckAsync();
        var dynamicSave = designer.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.Contains("/Composition/Save", StringComparison.Ordinal));
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        (await (await dynamicSave).JsonAsync())!.Value.GetProperty("message").GetString().Should().Be("Configuration saved.");
        await designer.GetByText("Configuration saved.", new() { Exact = true }).WaitForAsync();
        await designer.ReloadAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Dynamic text", Exact = true }).ClickAsync();
        var savedDynamicValue = designer.Locator(".composition-dynamic-item").First;
        (await savedDynamicValue.GetByLabel("Name", new() { Exact = true }).InputValueAsync()).Should().Be("Requested total");
        (await savedDynamicValue.GetByLabel("Source 1", new() { Exact = true }).Locator("option:checked").InnerTextAsync()).Should().Be("Requested amount (Number)");
        await CapturePane(designer, "dynamic-text");

        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}").ToString());
        await designer.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();
        var preview = designer.Locator("[data-preview-result]");
        await preview.Locator("[data-preview-runtime-form]").WaitForAsync();
        await preview.GetByLabel("Requested amount", new() { Exact = true }).FillAsync("700");
        await preview.GetByLabel("Matching amount", new() { Exact = true }).FillAsync("400");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Update preview and scoring", Exact = true }).ClickAsync();
        await preview.Locator(".qab-error-summary").GetByRole(AriaRole.Link, new() { Name = "The total budget must not exceed $1,000.", Exact = true }).WaitForAsync();
        await preview.GetByLabel("Matching amount", new() { Exact = true }).FillAsync("250");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Update preview and scoring", Exact = true }).ClickAsync();
        await preview.Locator(".qab-calculations").GetByText("950", new() { Exact = true }).WaitForAsync();
        (await preview.Locator(".qab-calculations").InnerTextAsync()).Should().Contain("Total budget").And.Contain("950");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Review your answers", Exact = true }).ClickAsync();
        await preview.Locator(".qab-review-summary").WaitForAsync();
        (await preview.Locator(".qab-review-summary").InnerTextAsync()).Should().Be("700");

        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}#readiness").ToString());
        var publish = designer.GetByRole(AriaRole.Button, new() { Name = "Publish version 1", Exact = true });
        await publish.ClickAsync();
        await designer.WaitForURLAsync("**/Applications/Detail/**");

        var filler = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        filler.PageError += (_, message) => errors.Add(message);
        await Login(filler, host.BaseAddress, "filler");
        await filler.GotoAsync(new Uri(host.BaseAddress, $"/App/{appId}").ToString());
        await filler.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire", Exact = true }).ClickAsync();
        await filler.WaitForURLAsync("**/App/Responses/**");
        var amountQuestion = filler.GetByLabel("Requested amount", new() { Exact = true });
        var matchingQuestion = filler.GetByLabel("Matching amount", new() { Exact = true });
        await SaveChange(filler, () => amountQuestion.FillAsync("700"));
        (await filler.Locator("[data-live-content] .qab-error-summary").CountAsync()).Should().Be(0);
        await SaveChange(filler, () => matchingQuestion.FillAsync("400"));
        var responseUrl = filler.Url;
        var failingAutosave = filler.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.EndsWith("/Save", StringComparison.Ordinal));
        await matchingQuestion.FillAsync("401");
        var failingResponse = await failingAutosave;
        failingResponse.Ok.Should().BeTrue();
        var failingPayload = await failingResponse.JsonAsync();
        failingPayload!.Value.GetProperty("liveValidationMessages").EnumerateArray().Select(message => message.GetString())
            .Should().ContainSingle().Which.Should().Be("The total budget must not exceed $1,000.");
        await filler.Locator("[data-live-content] .qab-error-summary").GetByRole(AriaRole.Link,
            new() { Name = "The total budget must not exceed $1,000.", Exact = true }).WaitForAsync();
        filler.Url.Should().Be(responseUrl, "live validation follows autosave without navigating away");
        (await matchingQuestion.EvaluateAsync<bool>("element => element === document.activeElement"))
            .Should().BeTrue("replacing the live fragment must preserve the respondent's focus");
        await CaptureRuntimeState(filler, "live-error");
        await SaveChange(filler, () => amountQuestion.FillAsync("500"));
        (await filler.Locator("[data-live-content] .qab-error-summary").CountAsync()).Should().Be(0,
            "changing the other contributing answer so the total is within the limit clears the live error");
        (await amountQuestion.EvaluateAsync<bool>("element => element === document.activeElement")).Should().BeTrue();
        await CaptureRuntimeState(filler, "live-clear");
        await SaveChange(filler, () => amountQuestion.FillAsync("700"));
        await filler.Locator("[data-live-content] .qab-error-summary").GetByRole(AriaRole.Link,
            new() { Name = "The total budget must not exceed $1,000.", Exact = true }).WaitForAsync();
        await SaveChange(filler, () => matchingQuestion.FillAsync("250"));
        (await filler.Locator("[data-live-content] .qab-error-summary").CountAsync()).Should().Be(0);
        (await matchingQuestion.EvaluateAsync<bool>("element => element === document.activeElement")).Should().BeTrue();
        foreach (var width in new[] { 1440, 375 })
        {
            await filler.SetViewportSizeAsync(width, 900);
            (await filler.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await filler.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-composition-filler-{width}.png"), FullPage = true });
        }
        await filler.GetByRole(AriaRole.Button, new() { Name = "Review your answers", Exact = true }).ClickAsync();
        await filler.GetByRole(AriaRole.Heading, new() { Name = "Review your answers" }).WaitForAsync();
        (await filler.Locator("main").InnerTextAsync()).Should().Contain("700").And.Contain("250");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Submit application", Exact = true }).ClickAsync();
        await filler.GetByRole(AriaRole.Heading, new() { Name = "Thank you", Exact = true }).WaitForAsync();
        (await host.CountSubmissionsAsync()).Should().Be(1);
        errors.Should().BeEmpty();
    }

    private static async Task Login(IPage page, Uri address, string username)
    {
        await page.GotoAsync(new Uri(address, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private static async Task SaveChange(IPage page, Func<Task> action)
    {
        var response = page.WaitForResponseAsync(result => result.Request.Method == "POST" && result.Url.EndsWith("/Save", StringComparison.Ordinal));
        await action();
        var result = await response;
        result.Ok.Should().BeTrue();
        await page.GetByText("Saved just now.", new() { Exact = true }).WaitForAsync();
    }

    private static async Task CapturePane(IPage page, string paneName, bool densityBound = false)
    {
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.EvaluateAsync("window.scrollTo(0, 0)");
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            if (densityBound && width == 1440)
            {
                var groupHeight = await page.Locator(".composition-item").Last.EvaluateAsync<double>("element => element.getBoundingClientRect().height");
                Console.WriteLine($"Composition group card height at 1440px: {groupHeight:0}px");
                groupHeight.Should().BeLessThan(650, "a two-row, two-column group should fit comfortably within a 900px desktop viewport");
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-composition-{paneName}-{width}.png"), FullPage = true });
        }
    }

    private static async Task CaptureRuntimeState(IPage page, string state)
    {
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-composition-{state}-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
}
