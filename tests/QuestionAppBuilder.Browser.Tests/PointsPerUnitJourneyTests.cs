using System.Globalization;
using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class PointsPerUnitJourneyTests
{
    [Fact]
    public async Task Designer_can_publish_per_unit_scoring_and_report_the_capped_submission_points()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var designer = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await Login(designer, host.BaseAddress, "designer");
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Applications/Detail/{applicationId}").ToString());
        await designer.Locator(".cv-add-rule summary").First.ClickAsync();
        var detailRule = designer.Locator(".cv-add-rule form[data-award-editor]").First;
        await detailRule.Locator("select[name='AwardMethod']").SelectOptionAsync("PointsPerUnit");
        (await detailRule.Locator("[data-per-unit-guidance]").InnerTextAsync()).Should()
            .Contain("Whole numbers only").And.Contain("Minimum value");
        (await detailRule.Locator("a").GetAttributeAsync("href")).Should()
            .Contain($"/ConfigureApp/Drafts/{applicationId}#answer-settings-");
        await detailRule.Locator("a").ClickAsync();
        await designer.Locator("details[id^='answer-settings-']:visible").WaitForAsync();

        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await designer.Locator(".aw-outline-element").First.ClickAsync();
        await designer.Locator(".aw-outline-action[href='#question-assets']").ClickAsync();
        await designer.Locator("#structure-question").SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        var prereqRule = designer.Locator("#question-assets [data-award-editor]");
        await prereqRule.Locator("select[name='awardMethod']").SelectOptionAsync("PointsPerUnit");
        (await prereqRule.Locator("[data-per-unit-guidance]").InnerTextAsync()).Should()
            .Contain("Whole numbers only").And.Contain("Minimum value");
        await prereqRule.Locator("input[name='pointsPerUnit']").FillAsync("3");
        await prereqRule.Locator("[data-per-unit-settings-link]").ClickAsync();
        var answerSettings = designer.Locator(".aw-element:visible details[id^='answer-settings']");
        await answerSettings.WaitForAsync();
        (await prereqRule.Locator("input[name='pointsPerUnit']").InputValueAsync()).Should().Be("3");
        await answerSettings.GetByLabel("Whole numbers only (Number questions)", new() { Exact = true }).CheckAsync();
        await answerSettings.Locator("input[name$='.MinValue']").FillAsync("0");
        await answerSettings.Locator("input[name$='.MaxValue']").FillAsync("100");
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync();

        await designer.Locator(".aw-outline-action[href='#question-assets']").ClickAsync();
        await designer.Locator("#structure-question").SelectOptionAsync(new SelectOptionValue { Label = "Requested amount" });
        var addRule = designer.Locator("#question-assets [data-award-editor]");
        await addRule.Locator("select[name='awardMethod']").SelectOptionAsync("PointsPerUnit");
        (await addRule.Locator("select[name='family']").InputValueAsync()).Should().Be("NumericRange");
        (await addRule.Locator("[data-per-unit-guidance]").InnerTextAsync()).Should().Contain("Requirements are met");
        (await addRule.Locator("[data-per-unit-settings-link]").GetAttributeAsync("href")).Should()
            .Be($"#answer-settings-{await designer.Locator("#structure-question").InputValueAsync()}");
        (await addRule.Locator("[data-fixed-award]").IsVisibleAsync()).Should().BeFalse();
        (await addRule.Locator("[data-per-unit-fields]").IsVisibleAsync()).Should().BeTrue();
        await addRule.Locator("input[name='pointsPerUnit']").FillAsync("3");
        await addRule.Locator("input[name='maximumPoints']").FillAsync("20");
        await addRule.Locator("input[name='minValue']").FillAsync("0");
        await addRule.Locator("input[name='maxValue']").FillAsync("100");
        var addedRule = designer.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddRule", StringComparison.Ordinal), new() { Timeout = 15000 });
        await addRule.GetByRole(AriaRole.Button, new() { Name = "Add scoring rule", Exact = true }).ClickAsync();
        (await addedRule).Status.Should().Be(302);

        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await designer.Locator(".aw-outline-element").First.ClickAsync();
        var reloadedSettings = designer.Locator(".aw-element:visible details[id^='answer-settings']");
        await reloadedSettings.Locator("summary").ClickAsync();
        (await reloadedSettings.GetByLabel("Whole numbers only (Number questions)", new() { Exact = true }).IsCheckedAsync()).Should().BeTrue();
        decimal.Parse(await reloadedSettings.Locator("input[name$='.MinValue']").InputValueAsync(), CultureInfo.InvariantCulture).Should().Be(0m);
        decimal.Parse(await reloadedSettings.Locator("input[name$='.MaxValue']").InputValueAsync(), CultureInfo.InvariantCulture).Should().Be(100m);
        await designer.Locator(".aw-element:visible details[id^='rules'] summary").ClickAsync();
        var savedRule = designer.Locator(".aw-element:visible .aw-rule").First;
        (await savedRule.Locator("select[name$='.AwardMethod']").InputValueAsync()).Should().Be("PointsPerUnit");
        (await savedRule.Locator("[data-fixed-award]").IsVisibleAsync()).Should().BeFalse();
        (await savedRule.Locator("[data-per-unit-fields]").IsVisibleAsync()).Should().BeTrue();
        (await savedRule.Locator("input[name$='.PointsPerUnit']").InputValueAsync()).Should().Be("3");
        (await savedRule.Locator("input[name$='.MaximumPoints']").InputValueAsync()).Should().Be("20");

        await reloadedSettings.Locator("select[name$='.AnswerType']").SelectOptionAsync("Textbox");
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync();
        (await savedRule.Locator("select[name$='.AwardMethod']").InputValueAsync()).Should().Be("PointsPerUnit");
        (await savedRule.Locator("[data-per-unit-guidance]").InnerTextAsync()).Should().Contain("Number question");
        (await designer.Locator("#draft-workspace").EvaluateAsync<string>("form => new FormData(form).get('Rules[0].AwardMethod') ?? ''"))
            .Should().Be("PointsPerUnit", "an ineligible currently selected award must remain successful form data");

        await designer.ReloadAsync();
        await designer.Locator(".aw-outline-element").First.ClickAsync();
        var invalidSettings = designer.Locator(".aw-element:visible details[id^='answer-settings']");
        await invalidSettings.Locator("summary").ClickAsync();
        (await invalidSettings.Locator("select[name$='.AnswerType']").InputValueAsync()).Should().Be("Textbox");
        await designer.Locator(".aw-element:visible details[id^='rules'] summary").ClickAsync();
        var invalidRule = designer.Locator(".aw-element:visible .aw-rule").First;
        (await invalidRule.Locator("select[name$='.AwardMethod']").InputValueAsync()).Should().Be("PointsPerUnit");
        await invalidSettings.Locator("select[name$='.AnswerType']").SelectOptionAsync("Number");
        await designer.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync();
        var savedRuleAfterRecovery = designer.Locator(".aw-element:visible .aw-rule").First;
        (await savedRuleAfterRecovery.Locator("select[name$='.AwardMethod']").InputValueAsync()).Should().Be("PointsPerUnit");
        (await savedRuleAfterRecovery.Locator("input[name$='.PointsPerUnit']").InputValueAsync()).Should().Be("3");
        (await savedRuleAfterRecovery.Locator("input[name$='.MaximumPoints']").InputValueAsync()).Should().Be("20");
        foreach (var width in new[] { 1440, 375 })
        {
            await designer.SetViewportSizeAsync(width, 900);
            (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await designer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-points-per-unit-{width}.png"), FullPage = true });
        }
        await designer.SetViewportSizeAsync(1440, 900);

        await designer.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true }).ClickAsync();
        var questionId = await designer.Locator("[data-preview-result] [data-preview-category='0'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        var answer = designer.Locator($"[name='Answers[{questionId}].Number']");
        await answer.FillAsync("9");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Update preview and scoring", Exact = true }).ClickAsync();
        await designer.Locator("[data-preview-result]").GetByText("Total score: 20", new() { Exact = true }).WaitForAsync();
        var scoreReport = designer.Locator("[data-preview-scoring-inspector]");
        await scoreReport.Locator(":scope > details > summary").ClickAsync();
        var questionScoring = scoreReport.GetByText("Question scoring", new() { Exact = true });
        await questionScoring.ClickAsync();
        await scoreReport.GetByText("Requested amount: 20 points", new() { Exact = true }).ClickAsync();
        await scoreReport.GetByText("Rule 1 · Numeric Range", new() { Exact = true }).ClickAsync();
        var scoreText = await scoreReport.InnerTextAsync();
        scoreText.Should().Contain("Units: 9; rate: 3; cap: 20 (reached)")
            .And.Contain("contributed 20 points");
        foreach (var width in new[] { 1440, 375 })
        {
            await designer.SetViewportSizeAsync(width, 900);
            (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await designer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-scoring-question-{width}.png"), FullPage = true });
        }
        await designer.SetViewportSizeAsync(1440, 900);

        await designer.GetByRole(AriaRole.Link, new() { Name = "Review & publish", Exact = true }).ClickAsync();
        await designer.Locator("#readiness:visible").WaitForAsync();
        var publish = designer.GetByRole(AriaRole.Button, new() { Name = "Publish version 1", Exact = true });
        await publish.WaitForAsync();
        (await publish.IsEnabledAsync()).Should().BeTrue(await designer.Locator("#readiness:visible").InnerTextAsync());
        await publish.ClickAsync();

        var filler = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await Login(filler, host.BaseAddress, "filler");
        await filler.GotoAsync(new Uri(host.BaseAddress, $"/App/{applicationId}").ToString());
        await filler.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire", Exact = true }).ClickAsync();
        await filler.GetByLabel("Requested amount", new() { Exact = true }).FillAsync("9");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Save & Continue", Exact = true }).ClickAsync();
        await filler.GetByLabel("Reason for applying", new() { Exact = true }).FillAsync("Build a community garden");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Review your answers", Exact = true }).ClickAsync();
        await filler.GetByRole(AriaRole.Button, new() { Name = "Submit application", Exact = true }).ClickAsync();
        await filler.GetByRole(AriaRole.Heading, new() { Name = "Thank you", Exact = true }).WaitForAsync();
        (await filler.Locator(".qab-score-total").InnerTextAsync()).Should().Be("20");

        var analyst = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await Login(analyst, host.BaseAddress, "analyst");
        await analyst.GotoAsync(new Uri(host.BaseAddress, "/Reports/Detail/1").ToString());
        (await analyst.GetByRole(AriaRole.Heading, new() { Name = "Submission 1", Exact = true }).InnerTextAsync()).Should().Be("Submission 1");
        var answerRow = analyst.Locator(".qab-report-table").Nth(1).GetByRole(AriaRole.Row).Filter(new() { HasText = "Requested amount" });
        (await answerRow.InnerTextAsync()).Should().Contain("9").And.Contain("20");
        (await analyst.Locator("body").InnerTextAsync()).Should().Contain("Stored scoring trace");
        (await host.CountSubmissionsAsync()).Should().Be(1);
    }

    private static async Task Login(IPage page, Uri address, string username)
    {
        await page.GotoAsync(new Uri(address, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
