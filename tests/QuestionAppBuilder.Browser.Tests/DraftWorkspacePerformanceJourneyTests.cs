using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class DraftWorkspacePerformanceJourneyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Draft_workspace_load_and_element_add_are_measured_separately_and_remain_usable()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedPerformanceDraftApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        page.SetDefaultNavigationTimeout(90_000);
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        var workspacePath = $"/ConfigureApp/Drafts/{applicationId}";
        var workspace = await MeasureRequestAsync(page,
            request => request.Method == "GET" && new Uri(request.Url).AbsolutePath == workspacePath,
            () => page.GotoAsync(new Uri(host.BaseAddress, workspacePath).ToString()));
        workspace.Response.Status.Should().Be((int)HttpStatusCode.OK);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Authoring workspace" }).WaitForAsync();
        var workspaceBytes = (await workspace.Response.BodyAsync()).Length;
        output.WriteLine($"Draft workspace GET (26 questions / 6 categories): {workspace.Elapsed.TotalMilliseconds:F1} ms, HTTP {workspace.Response.Status}, {workspaceBytes} body bytes.");

        await page.Locator(".aw-outline-action[href='#add-element']").ClickAsync();
        await page.Locator("[name='elementLabel']").FillAsync("Added performance question");
        await page.Locator("[name='elementType']").SelectOptionAsync("Number");
        var add = await MeasureRequestAsync(page,
            request => request.Method == "POST" && new Uri(request.Url).AbsolutePath.EndsWith("/Structure/AddElement", StringComparison.Ordinal),
            () => page.GetByRole(AriaRole.Button, new() { Name = "Add element" }).ClickAsync());
        add.Response.Status.Should().Be((int)HttpStatusCode.Redirect);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Questionnaire outline" })
            .GetByRole(AriaRole.Link, new() { Name = "Added performance question", Exact = true }).WaitForAsync();
        var addHeaders = await add.Response.AllHeadersAsync();
        var addBodySize = addHeaders.TryGetValue("content-length", out var contentLength) && long.TryParse(contentLength, out var parsedLength)
            ? $"{parsedLength} body bytes (Content-Length)"
            : "body unavailable on the 302 redirect";
        output.WriteLine($"AddElement POST only (redirect follow-up excluded): {add.Elapsed.TotalMilliseconds:F1} ms, HTTP {add.Response.Status}, {addBodySize}.");

        var readiness = await page.EvaluateAsync<string>("async () => { const response = await fetch(location.pathname + '/Readiness'); return `${response.status}:${await response.text()}`; }");
        readiness.Should().StartWith("200:").And.Contain("revision");

        await page.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        var displayName = page.Locator("[name='Version.DisplayName']");
        var autosave = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && new Uri(response.Url).AbsolutePath.EndsWith("/Save", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 15_000 });
        await displayName.FillAsync("Performance sample saved");
        var saveResponse = await autosave;
        saveResponse.Status.Should().Be((int)HttpStatusCode.OK, await saveResponse.TextAsync());
        await page.Locator("[data-save-status]").Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 15_000 });
        var refreshedReadiness = await page.EvaluateAsync<string>("async () => { const response = await fetch(location.pathname + '/Readiness'); return `${response.status}:${await response.text()}`; }");
        refreshedReadiness.Should().StartWith("200:").And.Contain("revision");
        (await displayName.InputValueAsync()).Should().Be("Performance sample saved");
    }

    private static async Task<(IResponse Response, TimeSpan Elapsed)> MeasureRequestAsync(
        IPage page, Func<IRequest, bool> matches, Func<Task> action)
    {
        var completion = new TaskCompletionSource<(IResponse Response, TimeSpan Elapsed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long startedAt = 0;
        EventHandler<IRequest> onRequest = (_, request) =>
        {
            if (matches(request)) Interlocked.Exchange(ref startedAt, Stopwatch.GetTimestamp());
        };
        EventHandler<IResponse> onResponse = (_, response) =>
        {
            if (!matches(response.Request)) return;
            var start = Interlocked.Read(ref startedAt);
            if (start != 0) completion.TrySetResult((response, Stopwatch.GetElapsedTime(start)));
        };

        page.Request += onRequest;
        page.Response += onResponse;
        try
        {
            await action();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            page.Request -= onRequest;
            page.Response -= onResponse;
        }
    }
}
