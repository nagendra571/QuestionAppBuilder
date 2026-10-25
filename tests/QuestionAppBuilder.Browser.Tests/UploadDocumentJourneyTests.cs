using FluentAssertions;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public class UploadDocumentJourneyTests
{
    [Fact]
    public async Task Document_upload_replace_remove_resume_review_and_report_download_work()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var appId = await host.SeedDocumentApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var designer = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await Login(designer, host.BaseAddress, "designer");
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}").ToString());
        await designer.Locator(".aw-outline-element").First.ClickAsync();
        await designer.Locator(".aw-element:visible details[id^='answer-settings'] summary").ClickAsync();
        (await designer.Locator(".aw-element:visible select[name$='.AnswerType'] option:checked").InnerTextAsync()).Should().Be("Upload Document");
        await designer.GetByRole(AriaRole.Link, new() { Name = "Preview", Exact = true }).ClickAsync();
        var preview = designer.Locator("[data-preview-result]");
        var evaluate = designer.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview", Exact = true });
        await evaluate.ClickAsync();
        await preview.Locator("[data-upload-document]").WaitForAsync();
        (await preview.Locator("[data-upload-document]").IsDisabledAsync()).Should().BeTrue();
        await preview.GetByLabel("Simulate an uploaded document", new() { Exact = true }).CheckAsync();
        await evaluate.ClickAsync();
        (await host.CountResponsesAsync()).Should().Be(0);
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{appId}#readiness").ToString());
        await designer.GetByRole(AriaRole.Button, new() { Name = "Publish version 1", Exact = true }).ClickAsync();
        await designer.WaitForURLAsync("**/Applications/Detail/**");
        var filler = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        var errors = new List<string>();
        filler.PageError += (_, message) => errors.Add(message);
        await Login(filler, host.BaseAddress, "filler");
        await filler.GotoAsync(new Uri(host.BaseAddress, $"/App/{appId}").ToString());
        await filler.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await filler.WaitForURLAsync("**/App/Responses/**");
        var responseUrl = filler.Url;
        await filler.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await filler.GetByText("Please upload your supporting document.", new() { Exact = true }).Last.WaitForAsync();
        var input = filler.Locator("[data-upload-document]");
        await input.SetInputFilesAsync(new FilePayload { Name = "invalid.exe", MimeType = "application/octet-stream", Buffer = "invalid"u8.ToArray() });
        await filler.GetByText("Choose a valid PDF, Word (.docx), or UTF-8 plain-text (.txt) document. Other formats and macro-enabled documents are not supported.", new() { Exact = true }).WaitForAsync();
        await Upload(filler, "budget.pdf", "%PDF-1.7\nBudget");
        (await filler.GetByRole(AriaRole.Link, new() { Name = "budget.pdf", Exact = true }).CountAsync()).Should().Be(1);
        (await input.IsEnabledAsync()).Should().BeTrue();
        (await filler.GetByText("Simulate an uploaded document", new() { Exact = true }).CountAsync()).Should().Be(0);
        await filler.ReloadAsync();
        await filler.GetByRole(AriaRole.Link, new() { Name = "budget.pdf", Exact = true }).WaitForAsync();
        await Upload(filler, "notes.txt", "Supporting notes");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Remove notes.txt", Exact = true }).ClickAsync();
        await filler.GetByText("Document removed.", new() { Exact = true }).WaitForAsync();
        (await filler.Locator(".qab-document-current").CountAsync()).Should().Be(0);
        await Upload(filler, "final.pdf", "%PDF-1.7\nFinal budget");
        foreach (var width in new[] { 1440, 375 })
        {
            await filler.SetViewportSizeAsync(width, 900);
            (await filler.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await filler.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"qab-upload-document-{width}.png"), FullPage = true });
        }
        var download = await filler.RunAndWaitForDownloadAsync(() => filler.GetByRole(AriaRole.Link, new() { Name = "final.pdf", Exact = true }).ClickAsync());
        download.SuggestedFilename.Should().Be("final.pdf");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await filler.GetByLabel("Reason for applying", new() { Exact = true }).FillAsync("Community support");
        await filler.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await filler.GetByRole(AriaRole.Link, new() { Name = "final.pdf", Exact = true }).WaitForAsync();
        await filler.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();
        await filler.GetByRole(AriaRole.Heading, new() { Name = "Thank you", Exact = true }).WaitForAsync();
        await filler.GotoAsync(responseUrl);
        await filler.WaitForURLAsync("**/Submitted");
        var analyst = await (await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();
        await Login(analyst, host.BaseAddress, "analyst");
        await analyst.GotoAsync(new Uri(host.BaseAddress, "/Reports/Detail/1").ToString());
        await analyst.GetByRole(AriaRole.Link, new() { Name = "final.pdf", Exact = true }).WaitForAsync();
        var reportHref = await analyst.GetByRole(AriaRole.Link, new() { Name = "final.pdf", Exact = true }).GetAttributeAsync("href");
        var reportResponse = await analyst.Context.APIRequest.GetAsync(new Uri(host.BaseAddress, reportHref!).ToString());
        reportResponse.Status.Should().Be(200, $"{reportHref}: {await reportResponse.TextAsync()}");
        (await reportResponse.BodyAsync()).Should().Equal("%PDF-1.7\nFinal budget"u8.ToArray());
        var forbidden = await filler.Context.APIRequest.GetAsync(new Uri(host.BaseAddress, reportHref!).ToString());
        forbidden.Status.Should().Be(403);
        var reportDownload = await analyst.RunAndWaitForDownloadAsync(() => analyst.GetByRole(AriaRole.Link, new() { Name = "final.pdf", Exact = true }).ClickAsync());
        reportDownload.SuggestedFilename.Should().Be("final.pdf");
        errors.Should().BeEmpty();
        (await host.CountSubmissionsAsync()).Should().Be(1);
    }

    private static async Task Upload(IPage page, string name, string text)
    {
        await page.Locator("[data-upload-document]").SetInputFilesAsync(new FilePayload { Name = name, MimeType = "application/octet-stream", Buffer = System.Text.Encoding.UTF8.GetBytes(text) });
        await page.GetByText("Document uploaded and saved.", new() { Exact = true }).WaitForAsync();
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
