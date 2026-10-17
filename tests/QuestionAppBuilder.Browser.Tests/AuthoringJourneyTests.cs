using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class AuthoringJourneyTests
{
    [Fact]
    public async Task Designer_can_create_and_edit_a_draft_and_keep_local_input_after_conflict()
    {
        await using var host = await SampleHostFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        page.SetDefaultNavigationTimeout(90_000);

        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
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
        await page.GetByRole(AriaRole.Heading, new() { Name = "Questionnaire settings" }).WaitForAsync();
        var cssStatus = await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Editor/css/editor.css')).status");
        var jsStatus = await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Editor/js/editor.js')).status");
        cssStatus.Should().Be((int)HttpStatusCode.OK);
        jsStatus.Should().Be((int)HttpStatusCode.OK);

        await page.Locator("[name='categoryName']").FillAsync("Applicant details");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add category" }).ClickAsync();
        (await page.Locator("[name='Categories[0].Name']").InputValueAsync()).Should().Be("Applicant details");
        await page.Locator("[name='elementLabel']").FillAsync("Application method");
        await page.Locator("[name='elementType']").SelectOptionAsync("RadioGroup");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add element" }).ClickAsync();
        (await page.Locator("[name='Elements[0].Label']").InputValueAsync()).Should().Be("Application method");
        await page.Locator("[name='optionLabel']").FillAsync("Online");
        await page.Locator("[name='optionValue']").FillAsync("online");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add option" }).ClickAsync();
        (await page.Locator("[name='Options[0].Label']").InputValueAsync()).Should().Be("Online");
        var duplicateButton = page.GetByRole(AriaRole.Button, new() { Name = "Duplicate element" });
        var duplicateResponseTask = page.WaitForResponseAsync(response => response.Url.Contains("/Structure/DuplicateElement", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        var duplicateWorkspaceTask = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await duplicateButton.ClickAsync();
        (await duplicateResponseTask).Status.Should().Be((int)HttpStatusCode.OK);
        (await duplicateWorkspaceTask).Status.Should().Be((int)HttpStatusCode.OK);
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
        (await page.GetByRole(AriaRole.Status).InnerTextAsync()).Should().Contain("The draft changed elsewhere");
        (await displayName.InputValueAsync()).Should().Be("Retain this value after a conflict");
        await context.CloseAsync();
    }

}
