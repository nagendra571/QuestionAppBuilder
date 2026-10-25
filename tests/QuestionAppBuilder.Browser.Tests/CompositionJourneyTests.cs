using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public class CompositionJourneyTests
{
    [Fact]
    public async Task Composition_changes_require_save_and_newer_edits_remain_dirty_after_a_delayed_save()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var appId = await host.SeedCompositionApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var designer = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await Login(designer, host.BaseAddress, "designer");
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{appId}").ToString());

        await designer.GetByRole(AriaRole.Heading, new() { Name = "Groups, rules & dynamic text", Exact = true }).WaitForAsync();
        var areaNav = designer.GetByRole(AriaRole.Navigation, new() { Name = "Questionnaire areas" });
        (await areaNav.GetByText("Groups, rules & dynamic text", new() { Exact = true }).GetAttributeAsync("aria-current")).Should().Be("page");
        (await designer.GetByText("Changes on this page are saved only when you select Save configuration.").CountAsync()).Should().Be(1);
        foreach (var width in new[] { 1440, 375 })
        {
            await designer.SetViewportSizeAsync(width, 900);
            (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue($"configuration navigation fits at {width}px");
            await designer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-configuration-navigation-{width}.png"), FullPage = false });
        }
        await designer.SetViewportSizeAsync(320, 800);
        (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue("configuration navigation fits at 320px");
        await designer.SetViewportSizeAsync(1280, 900);

        var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseThirdSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        await designer.RouteAsync("**/ConfigureApp/Composition/Save/*", async route =>
        {
            var currentSave = Interlocked.Increment(ref saveCount);
            if (currentSave == 1)
            {
                firstSaveStarted.TrySetResult();
                await releaseFirstSave.Task;
            }
            else if (currentSave == 2) secondSaveStarted.TrySetResult();
            else if (currentSave == 3)
            {
                thirdSaveStarted.TrySetResult();
                await releaseThirdSave.Task;
                await route.FulfillAsync(new RouteFulfillOptions { Status = (int)System.Net.HttpStatusCode.Conflict,
                    ContentType = "application/json", Body = "{\"message\":\"This draft changed elsewhere. Reload before saving; your edits are still shown.\"}" });
                return;
            }
            else if (currentSave == 4) fourthSaveStarted.TrySetResult();
            await route.ContinueAsync();
        });
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add group", Exact = true }).ClickAsync();
        var group = designer.Locator(".composition-item").Last;
        var name = group.GetByLabel("Name", new() { Exact = true });
        await name.FillAsync("First saved value");
        await group.GetByLabel("Question to add", new() { Exact = true })
            .SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "Select Save configuration to keep them." }).WaitForAsync();
        saveCount.Should().Be(0, "typing and adding configuration items require an explicit save");
        var unloadBlocked = await designer.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        unloadBlocked.Should().BeTrue();
        var firstResponse = designer.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Composition/Save", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 15000 });
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        await firstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await name.FillAsync("Newer value entered during save");
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saving configuration. Newer edits still need saving." }).WaitForAsync();
        releaseFirstSave.TrySetResult();
        var firstSaveResponse = await firstResponse;
        firstSaveResponse.Status.Should().Be((int)System.Net.HttpStatusCode.OK, await firstSaveResponse.TextAsync());
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved. Newer edits still need saving." }).WaitForAsync();

        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        await secondSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await designer.GetByText("Configuration saved.", new() { Exact = true }).WaitForAsync();
        saveCount.Should().Be(2);
        var cleanUnloadBlocked = await designer.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        cleanUnloadBlocked.Should().BeFalse();

        var savedValue = await name.InputValueAsync();
        await name.FillAsync("Temporary value sent during a failed save");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        await thirdSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await name.FillAsync(savedValue);
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saving configuration. Newer edits still need saving." }).WaitForAsync();
        var revertUnloadBlocked = await designer.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        revertUnloadBlocked.Should().BeTrue("the sent value may still be committed even though the editor reverted to the previous value");
        releaseThirdSave.TrySetResult();
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "This draft changed elsewhere. Reload before saving; your edits are still shown." }).WaitForAsync();
        var failedSaveUnloadBlocked = await designer.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        failedSaveUnloadBlocked.Should().BeTrue("a failed response leaves the committed state uncertain");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        await fourthSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await designer.GetByText("Configuration saved.", new() { Exact = true }).WaitForAsync();
        saveCount.Should().Be(4);
        var retriedUnloadBlocked = await designer.EvaluateAsync<bool>("() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }");
        retriedUnloadBlocked.Should().BeFalse("a confirmed successful save clears the uncertain result");

        await designer.ReloadAsync();
        group = designer.Locator(".composition-item").Last;
        (await group.GetByLabel("Name", new() { Exact = true }).InputValueAsync()).Should().Be("Newer value entered during save");

        await designer.EvaluateAsync("window.confirm = () => false");
        await group.GetByRole(AriaRole.Button, new() { Name = "Delete group", Exact = true }).ClickAsync();
        await designer.GetByText("All changes saved.", new() { Exact = true }).WaitForAsync();
    }

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
        var validationMessage = rule.GetByLabel("Validation message", new() { Exact = true });
        await validationMessage.FillAsync("The total budget must not exceed $1,000.");
        (await rule.Locator("summary.composition-summary").InnerTextAsync()).Should().Contain("Incomplete",
            "the collapsed rule summary should not imply an incomplete rule is ready");
        await validationMessage.FillAsync("<img src=x onerror=window.__ruleSummaryXss=1>");
        var ruleDescription = rule.Locator(".composition-rule-description");
        (await ruleDescription.InnerTextAsync()).Should().Contain("<img src=x onerror=window.__ruleSummaryXss=1>");
        (await ruleDescription.Locator("img, script").CountAsync()).Should().Be(0, "designer text must remain text in the description");
        (await validationMessage.EvaluateAsync<bool>("element => element === document.activeElement")).Should().BeTrue("updating a summary must not replace its active editor");
        await validationMessage.FillAsync("The total budget must not exceed $1,000.");
        var checks = rule.Locator("fieldset").Last;
        await checks.GetByRole(AriaRole.Button, new() { Name = "Add check", Exact = true }).ClickAsync();
        var check = rule.Locator("fieldset").Last;
        await check.GetByLabel("Value", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Calculated: Total budget" });
        await check.GetByLabel("Comparison", new() { Exact = true }).SelectOptionAsync("LessThanOrEqual");
        await check.GetByLabel("Number", new() { Exact = true }).FillAsync("1000");
        var conditions = rule.Locator("fieldset").First;
        await conditions.GetByRole(AriaRole.Button, new() { Name = "Add check", Exact = true }).ClickAsync();
        var condition = rule.Locator("fieldset").First.Locator(".composition-check").Last;
        await condition.GetByLabel("Value", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        await condition.GetByLabel("Comparison", new() { Exact = true }).SelectOptionAsync("GreaterThan");
        await condition.GetByLabel("Number", new() { Exact = true }).FillAsync("0");
        var validationSummary = rule.Locator("summary.composition-summary");
        (await validationSummary.InnerTextAsync()).Should().Contain("Require: calculated Total budget is at most 1,000")
            .And.Contain("show message when unmet").And.Contain("applies when the condition matches");
        var descriptionText = await ruleDescription.InnerTextAsync();
        descriptionText.Should().Contain("Target: first visible question in Budget")
            .And.Contain("Applies when: all of: Requested amount is greater than 0")
            .And.Contain("Must satisfy: all of: calculated Total budget is at most 1,000")
            .And.Contain("Message when unmet: The total budget must not exceed $1,000.")
            .And.Contain("Checked during navigation and submission");
        await check.GetByLabel("Comparison", new() { Exact = true }).SelectOptionAsync("GreaterThan");
        (await validationSummary.InnerTextAsync()).Should().Contain("Require: calculated Total budget is greater than 1,000", "comparison edits should update the summary immediately");
        await check.GetByLabel("Comparison", new() { Exact = true }).SelectOptionAsync("LessThanOrEqual");
        await rule.GetByLabel("Required checks", new() { Exact = true }).SelectOptionAsync("true");
        (await ruleDescription.InnerTextAsync()).Should().Contain("Must satisfy: any of:");
        await rule.GetByLabel("Required checks", new() { Exact = true }).SelectOptionAsync("false");
        await rule.GetByLabel("Purpose", new() { Exact = true }).SelectOptionAsync("Score");
        await rule.GetByLabel("Points", new() { Exact = true }).FillAsync("5");
        await rule.GetByLabel("Scoring band name (optional)", new() { Exact = true }).FillAsync("Tier A  ");
        (await rule.Locator("summary.composition-summary").InnerTextAsync()).Should().Contain("Award 5 points").And.Contain("first in “Tier A” band");
        (await rule.Locator(".composition-rule-description").InnerTextAsync()).Should().Contain("first matching rule in this category")
            .And.Contain("case-insensitive");
        await rule.GetByLabel("Points", new() { Exact = true }).FillAsync("1.5");
        (await rule.Locator("summary.composition-summary").InnerTextAsync()).Should().Contain("Incomplete — points must be a whole number");
        await rule.GetByLabel("Points", new() { Exact = true }).FillAsync("5");
        await rule.GetByLabel("Purpose", new() { Exact = true }).SelectOptionAsync("Validation");

        await designer.GetByRole(AriaRole.Button, new() { Name = "Calculated fields", Exact = true }).ClickAsync();
        var calculationEditor = designer.Locator(".composition-item").Last;
        if (await calculationEditor.GetAttributeAsync("open") is null) await calculationEditor.Locator("summary").ClickAsync();
        await calculationEditor.GetByLabel("Name", new() { Exact = true }).FillAsync("Combined total");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Combined rules", Exact = true }).ClickAsync();
        (await validationSummary.InnerTextAsync()).Should().Contain("calculated Combined total", "rule references should use the current calculation name");

        await designer.GetByRole(AriaRole.Button, new() { Name = "Calculated fields", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add calculated field", Exact = true }).ClickAsync();
        var temporaryCalculation = designer.Locator(".composition-item").Last;
        await temporaryCalculation.GetByLabel("Name", new() { Exact = true }).FillAsync("Temporary incomplete");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Combined rules", Exact = true }).ClickAsync();
        await check.GetByLabel("Value", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Calculated: Temporary incomplete" });
        await designer.GetByRole(AriaRole.Button, new() { Name = "Calculated fields", Exact = true }).ClickAsync();
        var incompleteCalculation = designer.Locator(".composition-item").Last;
        if (await incompleteCalculation.GetAttributeAsync("open") is null) await incompleteCalculation.Locator("summary").ClickAsync();
        await incompleteCalculation.GetByRole(AriaRole.Button, new() { Name = "Delete calculation", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Combined rules", Exact = true }).ClickAsync();
        (await validationSummary.InnerTextAsync()).Should().Contain("Incomplete");
        (await ruleDescription.InnerTextAsync()).Should().Contain("Missing calculation — choose another");
        await check.GetByLabel("Value", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Calculated: Combined total" });
        await designer.GetByRole(AriaRole.Button, new() { Name = "Calculated fields", Exact = true }).ClickAsync();
        var calculationToRestore = designer.Locator(".composition-item").Last;
        if (await calculationToRestore.GetAttributeAsync("open") is null) await calculationToRestore.Locator("summary").ClickAsync();
        await calculationToRestore.GetByLabel("Name", new() { Exact = true }).FillAsync("Total budget");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Combined rules", Exact = true }).ClickAsync();
        (await validationSummary.InnerTextAsync()).Should().Contain("calculated Total budget");

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
        await designer.SetViewportSizeAsync(1440, 900);
        await savedRule.Locator("summary.composition-summary").ClickAsync();
        await designer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-composition-rule-summary-1440.png"), FullPage = true });
        await designer.SetViewportSizeAsync(375, 900);
        (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue("rule summaries should fit narrow screens");
        (await savedRule.Locator("summary.composition-summary").InnerTextAsync()).Should().Contain("Require: calculated Total budget is at most 1,000");
        await designer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "qab-composition-rule-summary-375.png"), FullPage = true });
        await savedRule.Locator("summary.composition-summary").ClickAsync();
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
        await Assertions.Expect(filler.Locator(".qab-save-status")).ToHaveTextAsync("Saved just now.");
        (await matchingQuestion.InputValueAsync()).Should().Be("401", "the latest autosaved answer should be rendered before focus is checked");
        await filler.Locator("[data-live-content] .qab-error-summary").GetByRole(AriaRole.Link,
            new() { Name = "The total budget must not exceed $1,000.", Exact = true }).WaitForAsync();
        filler.Url.Should().Be(responseUrl, "live validation follows autosave without navigating away");
        await Assertions.Expect(matchingQuestion).ToBeFocusedAsync();
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
