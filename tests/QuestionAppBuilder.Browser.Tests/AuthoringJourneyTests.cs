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
    public async Task Preview_keeps_answers_across_sequential_categories_and_shows_runtime_receipt()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedDraftPreviewApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, $"/ConfigureApp/Drafts/{applicationId}").ToString());
        await page.GetByRole(AriaRole.Heading, new() { Name = "Questionnaire settings" }).WaitForAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        await preview.Locator("[data-preview-category='0'] [data-question-id]").First.WaitForAsync();
        var firstQuestionId = await preview.Locator("[data-preview-category='0'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        var number = preview.Locator($"[name='Answers[{firstQuestionId}].Number']");
        var blockedEvaluateTask = page.WaitForResponseAsync(response => response.Url.Contains("/Preview/", StringComparison.Ordinal)
            && response.Url.EndsWith("/Evaluate", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        var blockedEvaluate = await blockedEvaluateTask;
        var blockedBody = await blockedEvaluate.TextAsync();
        blockedEvaluate.Status.Should().Be(200, blockedBody);
        (await preview.InnerTextAsync()).Should().Contain("Complete the required fields before continuing.", blockedBody);
        (await preview.Locator(".qab-runtime-preview").GetAttributeAsync("data-preview-category")).Should().Be("0");

        await number.FillAsync("5");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await preview.Locator("[data-preview-category='1'] [data-question-id]").WaitForAsync();
        var secondQuestionId = await preview.Locator("[data-preview-category='1'] [data-question-id]").First.GetAttributeAsync("data-question-id");
        await preview.Locator($"[name='Answers[{secondQuestionId}].Text']").FillAsync("To serve my community");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await preview.GetByRole(AriaRole.Heading, new() { Name = "Review your answers" }).WaitForAsync();
        var reviewText = await preview.InnerTextAsync();
        reviewText.Should().Contain("5").And.Contain("To serve my community");

        await preview.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();
        await preview.GetByRole(AriaRole.Heading, new() { Name = "Thank you" }).WaitForAsync();
        (await preview.InnerTextAsync()).Should().Contain("Total score: 0")
            .And.Contain("This is a preview. No response or submission was created.");
        (await host.CountResponsesAsync()).Should().Be(0);
        (await host.CountSubmissionsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Designer_can_create_and_edit_a_draft_and_keep_local_input_after_conflict()
    {
        await using var host = await SampleHostFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, message) => pageErrors.Add(message);
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
        var editorScript = await page.EvaluateAsync<string>("async () => await (await fetch('/_content/QuestionAppBuilder.Editor/js/editor.js', { cache: 'no-store' })).text()");
        editorScript.Should().Contain("const saveCurrentDraft", "the isolated host must serve the implementation under test");

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

        await page.Locator("[name='elementLabel']").FillAsync("Requested amount");
        await page.Locator("[name='elementType']").SelectOptionAsync("Number");
        var addNumberResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddElement", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Add element" }).ClickAsync();
        var addNumberResponse = await addNumberResponseTask;
        addNumberResponse.Status.Should().Be((int)HttpStatusCode.Redirect);
        await page.GetByText("Requested amount").First.WaitForAsync();
        var numericQuestionId = await page.Locator("#structure-question option").Last.GetAttributeAsync("value");
        numericQuestionId.Should().NotBeNullOrWhiteSpace();
        await page.Locator("#structure-question").SelectOptionAsync(numericQuestionId!);
        await page.Locator("select[name='family']").SelectOptionAsync("NumericRange");
        await page.Locator("input[name='points']").FillAsync("1");
        await page.Locator("input[name='minValue']").FillAsync("0");
        await page.Locator("input[name='maxValue']").FillAsync("10");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        var addRuleRequests = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Structure/AddRule", StringComparison.Ordinal))
                addRuleRequests.Add(request.Url);
        };
        var firstRuleResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddRule", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Add scoring rule" }).ClickAsync();
        IResponse firstRuleResponse;
        try { firstRuleResponse = await firstRuleResponseTask; }
        catch (TimeoutException exception)
        {
            var state = await page.EvaluateAsync<string>("() => JSON.stringify({ url: location.href, active: document.activeElement?.outerHTML, status: document.querySelector('[data-save-status]')?.textContent, readiness: document.querySelector('[data-readiness-status]')?.textContent, ruleButton: [...document.querySelectorAll('button')].find(button => button.textContent.includes('Add scoring rule'))?.outerHTML, form: document.querySelector('#draft-workspace')?.action, invalid: [...(document.querySelector('#draft-workspace')?.querySelectorAll(':invalid') || [])].map(input => ({ name: input.name, message: input.validationMessage })) })");
            throw new TimeoutException($"AddRule did not receive a response. Browser state: {state}; page errors: {string.Join(" | ", pageErrors)}{Environment.NewLine}{host.GetDiagnosticLogs()}", exception);
        }
        firstRuleResponse.Status.Should().Be((int)HttpStatusCode.Redirect);
        addRuleRequests.Should().ContainSingle("one click on Add scoring rule must submit exactly one structure request");
        await page.GetByText("No scoring rules are defined.").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Locator("#structure-question").SelectOptionAsync(numericQuestionId!);
        await page.Locator("select[name='family']").SelectOptionAsync("NumericRange");
        await page.Locator("input[name='points']").FillAsync("2");
        await page.Locator("input[name='minValue']").FillAsync("5");
        await page.Locator("input[name='maxValue']").FillAsync("15");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        var secondRuleResponseTask = page.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("/Structure/AddRule", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Add scoring rule" }).ClickAsync();
        (await secondRuleResponseTask).Status.Should().Be((int)HttpStatusCode.Redirect);
        await page.WaitForTimeoutAsync(300);
        var afterRules = await page.Locator("body").InnerTextAsync();
        afterRules.Should().Contain("Numeric rules overlap; only the first matching rule contributes.", $"workspace after adding overlapping rules: {afterRules}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate preview" }).ClickAsync();
        var previewQuestion = page.Locator($"[data-preview-result] [data-question-id='{numericQuestionId}']");
        await previewQuestion.WaitForAsync();
        (await previewQuestion.InnerTextAsync()).Should().Contain("Requested amount");
        await previewQuestion.Locator($"[name='Answers[{numericQuestionId}].Number']").FillAsync("7");
        await page.GetByRole(AriaRole.Button, new() { Name = "Update preview and scoring" }).ClickAsync();
        var preview = page.Locator("[data-preview-result]");
        await preview.GetByText("Total score: 1", new() { Exact = true }).WaitForAsync();
        string.Join(" ", await preview.Locator(".aw-preview-score details p").AllTextContentsAsync())
            .Should().Contain("Rule").And.Contain("contributes 1 points").And.Contain("does not contribute");
        var acknowledgments = page.Locator("#publish-draft input[name='AcknowledgedWarningCodes']");
        await acknowledgments.First.CheckAsync();
        (await acknowledgments.First.IsCheckedAsync()).Should().BeTrue();

        var refreshedReadiness = page.WaitForResponseAsync(response => response.Url.Contains("/Drafts/", StringComparison.Ordinal)
            && response.Url.Contains("/Readiness", StringComparison.Ordinal), new PageWaitForResponseOptions { Timeout = 10000 });
        var updatedName = page.Locator("[name='Version.DisplayName']");
        await updatedName.FillAsync("Changed after acknowledging warnings");
        var readinessResponse = await refreshedReadiness;
        readinessResponse.Status.Should().Be((int)HttpStatusCode.OK);
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        (await acknowledgments.CountAsync()).Should().Be(1);
        (await acknowledgments.First.IsCheckedAsync()).Should().BeFalse("a warning acknowledgment applies only to the revision that was reviewed");
        (await page.Locator("[data-readiness-status]").InnerTextAsync()).Should().Contain("Readiness checked against the saved revision");

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
