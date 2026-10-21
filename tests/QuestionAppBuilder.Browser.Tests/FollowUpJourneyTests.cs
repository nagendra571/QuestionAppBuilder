using FluentAssertions;
using Microsoft.Playwright;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Browser.Tests;

public class FollowUpJourneyTests
{
    [Theory]
    [InlineData(AnswerType.RadioGroup)]
    [InlineData(AnswerType.Checkboxes)]
    public async Task Designer_creates_other_branch_and_filler_reveals_hides_restores_and_submits_nested_answers(AnswerType type)
    {
        await using var host = await SampleHostFixture.StartAsync();
        var appId = await host.SeedFollowUpApplicationAsync(type);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var designer = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var errors = new List<string>();
        designer.PageError += (_, message) => errors.Add(message);
        await Login(designer, host.BaseAddress, "designer");
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}").ToString());
        await designer.Locator(".aw-outline-element").First.ClickAsync();
        var rootId = await designer.Locator(".aw-element:visible").GetAttributeAsync("data-question-id");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add Other option", Exact = true }).ClickAsync();
        await designer.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await designer.GetByRole(AriaRole.Link, new() { Name = "Add follow-up question for Other", Exact = true }).WaitForAsync();
        await AddFollowUp(designer, "Other organization type", "RadioGroup");
        var childId = await designer.Locator(".aw-element:visible").GetAttributeAsync("data-question-id");
        await designer.GetByRole(AriaRole.Button, new() { Name = "Add Other option", Exact = true }).ClickAsync();
        await designer.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await designer.GetByRole(AriaRole.Link, new() { Name = "Add follow-up question for Other", Exact = true }).WaitForAsync();
        await AddFollowUp(designer, "Please describe your organization", "Textarea");
        var nestedId = await designer.Locator(".aw-element:visible").GetAttributeAsync("data-question-id");
        (await designer.Locator(".aw-follow-up-note:visible").InnerTextAsync()).Should().Contain("Other organization type").And.Contain("Other");
        foreach (var width in new[] { 1440, 375 })
        {
            await designer.SetViewportSizeAsync(width, 900);
            (await designer.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await designer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-follow-up-editor-{type}-{width}.png"), FullPage = true });
        }
        if (type == AnswerType.RadioGroup)
        {
            await designer.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
            var preview = designer.Locator("[data-preview-result]");
            var evaluate = designer.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true });
            await evaluate.ClickAsync();
            await preview.Locator($"[data-question-id='{rootId}']").WaitForAsync();
            (await preview.Locator($"[data-question-id='{childId}']").CountAsync()).Should().Be(0);
            await preview.Locator($"[data-question-id='{rootId}']").GetByLabel("Other", new() { Exact = true }).CheckAsync();
            await evaluate.ClickAsync();
            await preview.Locator($"[data-question-id='{childId}']").WaitForAsync();
            await preview.Locator($"[data-question-id='{childId}']").GetByLabel("Other", new() { Exact = true }).CheckAsync();
            await evaluate.ClickAsync();
            await preview.Locator($"[data-question-id='{nestedId}']").WaitForAsync();
            (await host.CountResponsesAsync()).Should().Be(0);
        }
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Applications/Detail/{appId}").ToString());
        (await designer.Locator(".cv-follow-up").CountAsync()).Should().Be(2);
        (await designer.Locator("form[data-confirm]").CountAsync()).Should().BeGreaterThan(0);
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}#readiness").ToString());
        await designer.GetByRole(AriaRole.Button, new() { Name = "Publish version 1", Exact = true }).ClickAsync();
        await designer.WaitForURLAsync("**/Applications/Detail/**");
        (await designer.Locator(".cv-follow-up").CountAsync()).Should().Be(2);

        var filler = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        filler.PageError += (_, message) => errors.Add(message);
        await Login(filler, host.BaseAddress, "filler");
        await filler.GotoAsync(new Uri(host.BaseAddress, $"/App/{appId}").ToString());
        await filler.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await filler.WaitForURLAsync("**/App/Responses/**");
        var root = filler.Locator($"[data-question-id='{rootId}']");
        var child = filler.Locator($"[data-question-id='{childId}']");
        var nested = filler.Locator($"[data-question-id='{nestedId}']");
        (await child.CountAsync()).Should().Be(0);
        await SaveChange(filler, () => root.GetByLabel("Other", new() { Exact = true }).CheckAsync());
        await child.WaitForAsync();
        (await root.GetByLabel("Other", new() { Exact = true }).EvaluateAsync<bool>("element => element === document.activeElement")).Should().BeTrue();
        await SaveChange(filler, () => child.GetByLabel("Other", new() { Exact = true }).CheckAsync());
        await nested.WaitForAsync();
        (await nested.GetByLabel("Please describe your organization", new() { Exact = true }).EvaluateAsync<bool>("element => element.validity.valueMissing")).Should().BeTrue();
        // Exercise server validation as well as the browser's native required constraint.
        await filler.Locator("form[data-runtime-form]").EvaluateAsync("form => form.noValidate = true");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await filler.Locator(".qab-error-summary").WaitForAsync();
        (await filler.Locator(".qab-error-summary").InnerTextAsync()).Should().Contain("A response is required.");
        await SaveChange(filler, () => nested.GetByLabel("Please describe your organization", new() { Exact = true }).FillAsync("Community partnership"));
        (await nested.GetByLabel("Please describe your organization", new() { Exact = true }).InputValueAsync()).Should().Be("Community partnership", "the typed value must survive its first autosave");
        if (type == AnswerType.Checkboxes)
        {
            await SaveChange(filler, () => root.GetByLabel("Nonprofit", new() { Exact = true }).CheckAsync());
            (await nested.CountAsync()).Should().Be(1);
            await SaveChange(filler, () => root.GetByLabel("Other", new() { Exact = true }).UncheckAsync());
        }
        else await SaveChange(filler, () => root.GetByLabel("Nonprofit", new() { Exact = true }).CheckAsync());
        (await child.CountAsync()).Should().Be(0);
        (await nested.CountAsync()).Should().Be(0);
        (await filler.Locator($"input[type='hidden'][name='Answers[{nestedId}].Text']").InputValueAsync()).Should().Be("Community partnership", "hiding a branch must keep its draft value in the form");
        await SaveChange(filler, () => root.GetByLabel("Other", new() { Exact = true }).CheckAsync());
        await nested.WaitForAsync();
        (await nested.GetByLabel("Please describe your organization", new() { Exact = true }).InputValueAsync()).Should().Be("Community partnership");
        var responseId = Guid.Parse(new Uri(filler.Url).Segments.Last());
        (await host.ReadSavedAnswerAsync(responseId, int.Parse(nestedId!))).Should().Be("Community partnership", "the database must retain the answer before reloading");
        var reloaded = await filler.ReloadAsync();
        (await reloaded!.TextAsync()).Should().Contain("Community partnership", "the GET response must render the saved answer");
        (await nested.GetByLabel("Please describe your organization", new() { Exact = true }).InputValueAsync()).Should().Be("Community partnership");
        foreach (var width in new[] { 1440, 375 })
        {
            await filler.SetViewportSizeAsync(width, 900);
            (await filler.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await filler.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-follow-up-filler-{type}-{width}.png"), FullPage = true });
        }
        await filler.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await filler.GetByLabel("Reason for applying", new() { Exact = true }).FillAsync("To serve the community");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await filler.GetByRole(AriaRole.Heading, new() { Name = "Review your answers" }).WaitForAsync();
        (await filler.Locator("main").InnerTextAsync()).Should().Contain("Community partnership");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();
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

    private static async Task AddFollowUp(IPage page, string label, string answerType)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Add follow-up question for Other", Exact = true }).ClickAsync();
        var form = page.Locator("#add-follow-up-form");
        (await form.Locator("[name='parentOptionId'] option:checked").InnerTextAsync()).Should().EndWith("/ Other");
        await form.GetByLabel("Follow-up question", new() { Exact = true }).FillAsync(label);
        await form.GetByLabel("Answer type", new() { Exact = true }).SelectOptionAsync(answerType);
        var reload = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/") && !response.Url.Contains("/Readiness"));
        await form.GetByRole(AriaRole.Button, new() { Name = "Add follow-up question", Exact = true }).ClickAsync();
        await reload;
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        (await page.Locator(".aw-element:visible [data-item-title]").InnerTextAsync()).Should().Be(label);
    }

    private static async Task SaveChange(IPage page, Func<Task> action)
    {
        var response = page.WaitForResponseAsync(result => result.Request.Method == "POST" && result.Url.EndsWith("/Save"));
        await action();
        var result = await response;
        result.Ok.Should().BeTrue();
        await result.TextAsync();
        await page.GetByText("Saved just now.", new() { Exact = true }).WaitForAsync();
    }
}
