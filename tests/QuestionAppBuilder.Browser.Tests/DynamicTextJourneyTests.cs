using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public class DynamicTextJourneyTests
{
    [Fact]
    public async Task Dynamic_values_survive_authoring_and_render_in_question_help_review_and_receipt()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var appId = await host.SeedDynamicTextApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var designer = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var pageErrors = new List<string>();
        designer.PageError += (_, error) => pageErrors.Add(error);
        await Login(designer, host.BaseAddress, "designer");

        var compositionResponse = await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Composition/Index/{appId}").ToString());
        var pageText = (await designer.Locator("body").InnerTextAsync()).Replace(Environment.NewLine, " ");
        (await designer.Locator("[data-composition-form]").CountAsync()).Should().Be(1,
            $"GET returned {(int?)compositionResponse?.Status}; URL was {designer.Url}; page was {pageText[..Math.Min(800, pageText.Length)]}");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Dynamic text", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add value", Exact = true }).ClickAsync();
        var value = designer.Locator(".composition-dynamic-item").Nth(2);
        await Open(value);
        await value.GetByLabel("Name", new() { Exact = true }).FillAsync("Full name");
        await value.GetByLabel("Operation", new() { Exact = true }).SelectOptionAsync("JoinText");
        await value.GetByRole(AriaRole.Button, new() { Name = "Add source", Exact = true }).ClickAsync();
        await value.GetByRole(AriaRole.Button, new() { Name = "Add source", Exact = true }).ClickAsync();
        value = designer.Locator(".composition-dynamic-item").Nth(2);
        await value.GetByLabel("Source 1", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "First name (Textbox)" });
        await value.GetByLabel("Source 2", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Middle name (Textbox)" });
        await value.GetByLabel("Source 3", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Last name (Textbox)" });
        (await value.GetByLabel("Skip empty values", new() { Exact = true }).IsCheckedAsync()).Should().BeTrue();

        var contentLabel = designer.Locator(".composition-dynamic-item").Nth(5);
        await Open(contentLabel);
        await contentLabel.GetByLabel("Value to insert 2", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Value: Full name" });
        var save = designer.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Url.Contains("/Composition/Save", StringComparison.Ordinal));
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save configuration", Exact = true }).ClickAsync();
        var saveResponse = await save;
        saveResponse.Ok.Should().BeTrue(await saveResponse.TextAsync());
        await designer.GetByText("Configuration saved.", new() { Exact = true }).WaitForAsync();
        await designer.ReloadAsync();
        await designer.GetByRole(AriaRole.Button, new() { Name = "Dynamic text", Exact = true }).ClickAsync();
        value = designer.Locator(".composition-dynamic-item").Nth(2);
        await Open(value);
        (await value.GetByLabel("Name", new() { Exact = true }).InputValueAsync()).Should().Be("Full name");
        (await value.GetByLabel("Source 2", new() { Exact = true }).Locator("option:checked").InnerTextAsync()).Should().Be("Middle name (Textbox)");
        contentLabel = designer.Locator(".composition-dynamic-item").Nth(5);
        await Open(contentLabel);
        (await contentLabel.GetByLabel("Value to insert 2", new() { Exact = true }).Locator("option:checked").InnerTextAsync()).Should().Be("Value: Full name");
        await CaptureDynamicPane(designer);

        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}#readiness").ToString());
        await designer.GetByRole(AriaRole.Button, new() { Name = "Publish version 1", Exact = true }).ClickAsync();
        await designer.WaitForURLAsync("**/Applications/Detail/**");

        var respondent = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        respondent.PageError += (_, error) => pageErrors.Add(error);
        await Login(respondent, host.BaseAddress, "filler");
        await respondent.GotoAsync(new Uri(host.BaseAddress, $"/App/{appId}").ToString());
        await respondent.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire", Exact = true }).ClickAsync();
        await respondent.WaitForURLAsync("**/App/Responses/**");
        await SaveChange(respondent, () => respondent.GetByLabel("First name", new() { Exact = true }).FillAsync("Alex"));
        await SaveChange(respondent, () => respondent.GetByLabel("Last name", new() { Exact = true }).FillAsync("Chen"));
        var detailsLabel = respondent.Locator(".qab-content-panel");
        await detailsLabel.GetByText("Applicant details for Alex Chen", new() { Exact = true }).WaitForAsync();
        await SaveChange(respondent, () => respondent.Locator("input[type=date]").FillAsync("1990-05-12"));
        await respondent.GetByLabel("Date of birth for Alex", new() { Exact = true }).WaitForAsync();
        await respondent.GetByText("Birth date (ISO): 1990-05-12", new() { Exact = true }).WaitForAsync();
        await respondent.GetByRole(AriaRole.Button, new() { Name = "Review your answers", Exact = true }).ClickAsync();
        await respondent.GetByRole(AriaRole.Heading, new() { Name = "Review your answers", Exact = true }).WaitForAsync();
        var reviewSummary = respondent.Locator(".qab-review-summary");
        await reviewSummary.WaitForAsync();
        var summaryText = await reviewSummary.InnerTextAsync();
        summaryText.Should().Contain("Alex").And.Contain("Chen").And.Contain("May 12, 1990").And.Contain("age: 36");
        (await respondent.Locator(".qab-review-category").InnerTextAsync()).Should().Contain("05-12-1990", "date answers keep an ISO value for evaluation and use a separate review format");
        await respondent.GetByRole(AriaRole.Button, new() { Name = "Submit application", Exact = true }).ClickAsync();
        await respondent.GetByRole(AriaRole.Heading, new() { Name = "Thank you", Exact = true }).WaitForAsync();
        (await respondent.Locator(".qab-receipt").InnerTextAsync()).Should().Contain("Thank you, Alex Chen");

        foreach (var width in new[] { 1440, 375 })
        {
            await respondent.SetViewportSizeAsync(width, 900);
            (await respondent.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await respondent.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-dynamic-runtime-{width}.png"), FullPage = true });
        }
        pageErrors.Should().BeEmpty();
    }

    private static async Task Login(IPage page, Uri address, string username)
    {
        await page.GotoAsync(new Uri(address, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private static async Task Open(ILocator item)
    {
        if (await item.GetAttributeAsync("open") is null) await item.Locator("summary").ClickAsync();
    }

    private static async Task SaveChange(IPage page, Func<Task> action)
    {
        var response = page.WaitForResponseAsync(result => result.Request.Method == "POST" && result.Url.EndsWith("/Save", StringComparison.Ordinal));
        await action();
        (await response).Ok.Should().BeTrue();
        await page.GetByText("Saved just now.", new() { Exact = true }).WaitForAsync();
    }

    private static async Task CaptureDynamicPane(IPage page)
    {
        foreach (var width in new[] { 1440, 375 })
        {
            await page.SetViewportSizeAsync(width, 900);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-dynamic-editor-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
}
