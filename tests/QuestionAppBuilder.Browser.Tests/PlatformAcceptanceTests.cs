using Deque.AxeCore.Playwright;
using FluentAssertions;
using Microsoft.Playwright;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Runtime.Tests;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class PlatformAcceptanceTests
{
    [Fact]
    public async Task Complete_RFA_journey_exercises_six_inputs_two_scoring_modes_and_hidden_branch()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        host.Runtime.Graph = BuildRfaGraph("RFA 2026", version: 1);
        new DefinitionEvaluator().Evaluate(host.Runtime.Graph,
            new Dictionary<int, AnswerValue> { [1] = new("branch answered", null, null) }, false)
            .VisibleQuestionIds.Should().NotContain(7);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();

        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await page.Locator("[name='Answers[1].Text']").FillAsync("branch answered");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await page.Locator("[name='Answers[2].Text']").FillAsync("Narrative answer");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await page.Locator("[name='Answers[3].SelectedOptionIds']").SelectOptionAsync("31");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await page.Locator("[name='Answers[4].SelectedOptionIds'][value='41']").CheckAsync();
        await page.Locator("[name='Answers[5].SelectedOptionIds'][value='51']").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
        await page.Locator("[name='Answers[6].Number']").FillAsync("2");
        (await page.Locator("[name='Answers[1].Text']").InputValueAsync()).Should().Be("branch answered");
        var q7Html = await page.Locator("[name='Answers[7].Text']").EvaluateAllAsync<string>("elements => elements.map(element => element.outerHTML).join(' | ')");
        (await page.Locator("[name='Answers[7].Text']").CountAsync()).Should().Be(0, $"the answer to question 1 hides its dependent branch; found {q7Html}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Thank you" }).WaitForAsync();
        (await page.Locator("[data-total-score]").GetAttributeAsync("data-total-score")).Should().Be("14");
        host.Runtime.SubmissionCount.Should().Be(1);
        host.Runtime.SubmittedAnswers.Keys.Should().NotContain(7);
    }

    [Fact]
    public async Task Publishing_during_fill_keeps_old_version()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        host.Runtime.Graph = BuildRfaGraph("New publication", version: 2);
        (await page.GetByRole(AriaRole.Heading, new() { Name = "Test questionnaire" }).CountAsync()).Should().Be(1);
        await page.Locator("[name='Answers[1].Text']").FillAsync("pinned to version one");
        (await page.GetByRole(AriaRole.Heading, new() { Name = "Test questionnaire" }).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Email_link_request_uses_a_neutral_confirmation()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        host.Runtime.Graph = BuildRfaGraph("Email-enabled RFA", version: 1, RespondentAccessMode.EmailLink);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var first = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await first.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await first.GetByLabel("Email address").FillAsync("respondent@example.test");
        var emailResponse = first.WaitForResponseAsync(response => response.Url.Contains("/Access/Request", StringComparison.Ordinal));
        await first.GetByRole(AriaRole.Button, new() { Name = "Email me a link to start" }).ClickAsync();
        (await emailResponse).Status.Should().Be(200);
        (await first.Locator("body").InnerTextAsync()).Should().Contain("If the address matches a response, an access link has been sent");
    }

    [Fact]
    public async Task Email_resume_recovery_remains_available_when_new_responses_are_closed()
    {
        var runtime = new TestHostFixture.FakeRuntime { AcceptingNewResponses = false };
        runtime.Graph.Version.RespondentAccessMode = RespondentAccessMode.EmailLink;
        await using var host = await BrowserTestFixture.StartAsync(runtime);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();

        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());

        (await page.GetByLabel("Email me a link to my saved response").IsVisibleAsync()).Should().BeTrue();
        (await page.GetByRole(AriaRole.Button, new() { Name = "Send resume link" }).CountAsync()).Should().Be(1);
        (await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).CountAsync()).Should().Be(0);
        (await page.GetByRole(AriaRole.Button, new() { Name = "Email me a link to start" }).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Signed_in_response_resumes_for_owner_and_is_hidden_from_another_user()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedHostSignInApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var owner = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        owner.SetDefaultNavigationTimeout(90_000);
        await LoginAsync(owner, host.BaseAddress, "filler");
        await owner.GotoAsync(new Uri(host.BaseAddress, $"/App/{applicationId}").ToString());
        var startResponseTask = owner.WaitForResponseAsync(response => response.Url.EndsWith("/Start", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 90_000 });
        await owner.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        IResponse startResponse;
        try { startResponse = await startResponseTask; }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Start POST did not receive a response. SampleHost logs:{Environment.NewLine}{host.GetDiagnosticLogs()}", exception);
        }
        startResponse.Status.Should().Be(302, "a successful Start creates a response and redirects to it");
        await owner.WaitForURLAsync("**/App/Responses/**", new() { Timeout = 90_000 });
        var responseUrl = owner.Url;
        await owner.Locator("[name='Answers[1].Text']").FillAsync("owner-only saved value");
        await owner.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved just now." }).WaitForAsync(new() { Timeout = 5000 });

        await owner.Context.CloseAsync();
        var ownerResume = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(ownerResume, host.BaseAddress, "filler");
        await ownerResume.GotoAsync(responseUrl);
        (await ownerResume.Locator("[name='Answers[1].Text']").InputValueAsync()).Should().Be("owner-only saved value");

        var otherUser = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(otherUser, host.BaseAddress, "filler2");
        var otherResponse = await otherUser.GotoAsync(responseUrl);
        otherResponse!.Status.Should().Be(404, await otherResponse.TextAsync());
    }

    [Fact]
    public async Task Duplicate_submit_after_close_returns_same_receipt_without_extra_scores()
    {
        await using var host = await BrowserTestFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await page.GotoAsync(new Uri(host.BaseAddress, "/App/1").ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        var responseUrl = page.Url;
        for (var question = 1; question <= 5; question++)
        {
            await page.Locator($"[name='Answers[{question}].Text']").FillAsync($"answer {question}");
            if (question < 5) await page.GetByRole(AriaRole.Button, new() { Name = "Save & Continue" }).ClickAsync();
            else await page.GetByRole(AriaRole.Button, new() { Name = "Review your answers" }).ClickAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Send application" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Thank you" }).WaitForAsync();
        var firstReceipt = await page.Locator("[data-submission-id]").GetAttributeAsync("data-submission-id");
        await page.GotoAsync(responseUrl);
        var replayedReceipt = await page.Locator("[data-submission-id]").GetAttributeAsync("data-submission-id");
        replayedReceipt.Should().Be(firstReceipt);
        host.Runtime.SubmissionCount.Should().Be(1);
    }

    [Fact]
    public async Task Access_denied_on_cross_user_and_cross_role_routes()
    {
        await using var host = await SampleHostFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var designer = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(designer, host.BaseAddress, "designer");
        await designer.GetByRole(AriaRole.Heading, new() { Name = "Applications" }).WaitForAsync();
        (await designer.GotoAsync(new Uri(host.BaseAddress, "/Reports").ToString()))!.Status.Should().Be(403);

        var analyst = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(analyst, host.BaseAddress, "analyst");
        (await analyst.GotoAsync(new Uri(host.BaseAddress, "/Reports").ToString()))!.Status.Should().Be(200);
        (await analyst.GotoAsync(new Uri(host.BaseAddress, "/ConfigureApp/Applications").ToString()))!.Status.Should().Be(403);
    }

    [Fact]
    public async Task Host_fill_policy_rejects_authenticated_designer_from_creating_a_response()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedHostSignInApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var designer = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(designer, host.BaseAddress, "designer");
        await designer.GotoAsync(new Uri(host.BaseAddress, $"/App/{applicationId}").ToString());
        var startResponseTask = designer.WaitForResponseAsync(response => response.Url.EndsWith("/Start", StringComparison.Ordinal));

        await designer.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();

        var startResponse = await startResponseTask;
        startResponse.Status.Should().Be(403, $"unexpected Start response location: {startResponse.Headers.GetValueOrDefault("location", "<none>")}; host: {host.GetDiagnosticLogs()}");
        (await host.CountResponsesAsync()).Should().Be(0, "the host FillPolicy is enforced before a response is created");
    }

    [Fact]
    public async Task Designer_can_close_submissions_and_respondent_review_disables_submit()
    {
        await using var host = await SampleHostFixture.StartAsync();
        var applicationId = await host.SeedHostSignInApplicationAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var respondent = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(respondent, host.BaseAddress, "filler");
        await respondent.GotoAsync(new Uri(host.BaseAddress, $"/App/{applicationId}").ToString());
        await respondent.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).ClickAsync();
        await respondent.WaitForURLAsync("**/Responses/*");
        var responseUrl = new Uri(respondent.Url).GetLeftPart(UriPartial.Path);
        var initialReview = await respondent.GotoAsync($"{responseUrl}/Review");
        initialReview!.Status.Should().Be(200, $"review URL was {respondent.Url}; body: {await respondent.Locator("body").InnerTextAsync()}");
        var submitButton = respondent.Locator("form[data-runtime-form] button[type=submit]");
        (await submitButton.CountAsync()).Should().Be(1, $"review URL was {respondent.Url}; body: {await respondent.Locator("body").InnerTextAsync()}");
        (await submitButton.IsDisabledAsync()).Should().BeFalse();

        var designer = await (await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })).NewPageAsync();
        await LoginAsync(designer, host.BaseAddress, "designer");
        await designer.GotoAsync(new Uri(host.BaseAddress, "/ConfigureApp/Applications").ToString());
        var detailLink = designer.Locator(".editor-app-name").First;
        await detailLink.WaitForAsync();
        var detailUrl = await detailLink.GetAttributeAsync("href");
        detailUrl.Should().NotBeNullOrWhiteSpace();
        var detail = await designer.GotoAsync(new Uri(host.BaseAddress, detailUrl!).ToString());
        detail!.Status.Should().Be(200, $"designer detail URL was {designer.Url}; body: {await designer.Locator("body").InnerTextAsync()}");
        await designer.Locator("#accept-new-responses").UncheckAsync();
        await designer.Locator("#accept-submissions").UncheckAsync();
        var update = designer.WaitForResponseAsync(response => response.Request.Method == "POST"
            && response.Url.Contains("UpdateAvailability", StringComparison.Ordinal));
        await designer.GetByRole(AriaRole.Button, new() { Name = "Save availability" }).ClickAsync();
        (await update).Status.Should().Be(302);

        var availability = await host.ReadAvailabilityAsync(applicationId);
        availability.AcceptNewResponses.Should().BeFalse();
        availability.AcceptSubmissions.Should().BeFalse();
        availability.AuditCount.Should().Be(1);
        availability.ActorKey.Should().Contain(":designer");

        var closedReview = await respondent.GotoAsync($"{responseUrl}/Review");
        closedReview!.Status.Should().Be(200, $"review URL was {respondent.Url}; body: {await respondent.Locator("body").InnerTextAsync()}");
        await respondent.GetByText("Submissions are currently closed").WaitForAsync();
        (await submitButton.CountAsync()).Should().Be(1);
        (await submitButton.IsDisabledAsync()).Should().BeTrue();
        await respondent.GotoAsync(new Uri(host.BaseAddress, $"/App/{applicationId}").ToString());
        (await respondent.GetByRole(AriaRole.Button, new() { Name = "Start questionnaire" }).CountAsync()).Should().Be(0);
    }

    private static async Task LoginAsync(IPage page, Uri baseAddress, string username)
    {
        await page.GotoAsync(new Uri(baseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
    }

    [PackageConsumerFact]
    public async Task Installed_packages_render_assets_and_views_under_custom_prefix()
    {
        var packageHost = Environment.GetEnvironmentVariable("QAB_PACKAGE_HOST_URL");
        packageHost.Should().NotBeNullOrWhiteSpace();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{packageHost}/Builder/PackageProof/Editor");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Applications" }).WaitForAsync();
        await page.GotoAsync($"{packageHost}/Insights/PackageProof/Reports");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Submission analysis" }).WaitForAsync();
        foreach (var asset in new[] { "/_content/QuestionAppBuilder.Core/js/runtime.js", "/_content/QuestionAppBuilder.Editor/js/editor.js", "/_content/QuestionAppBuilder.Reports/css/reports.css" })
            (await page.EvaluateAsync<int>($"async () => (await fetch('{asset}')).status")).Should().Be(200);
    }

    private static ApplicationVersionGraph BuildRfaGraph(string name, int version, RespondentAccessMode accessMode = RespondentAccessMode.HostSignIn)
    {
        var versionEntity = new ApplicationVersion
        {
            Id = 10 + version, ApplicationId = 1, VersionNumber = version, Status = VersionStatus.Published,
            DisplayName = name, NavigationMode = NavigationMode.Sequential, ResultVisibility = ResultVisibility.Total,
            RespondentAccessMode = accessMode, SubmitLabel = "Submit application", ConfirmationText = "RFA received"
        };
        var categories = new List<Category>();
        Element AddQuestion(int categoryId, int questionId, string label, AnswerType type, bool required,
            IReadOnlyList<QuestionOption>? options = null, ScoringMode mode = ScoringMode.Additive, int sort = 1)
        {
            var question = new Question { Id = questionId, ElementId = questionId, AnswerType = type, IsRequired = required, ScoringMode = mode };
            if (options is not null) question.Options.AddRange(options);
            var element = new Element { Id = questionId, CategoryId = categoryId, Kind = ElementKind.Question, Label = label, SortOrder = sort, LogicalKey = Guid.NewGuid(), Question = question };
            return element;
        }

        var q1 = AddQuestion(1, 1, "Organization name", AnswerType.Textbox, false);
        q1.Question!.Rules.AddRange(new[] { new ScoringRule { Id = 101, QuestionId = 1, Family = RuleFamily.Presence, Points = 1, SortOrder = 1 }, new ScoringRule { Id = 102, QuestionId = 1, Family = RuleFamily.Presence, Points = 2, SortOrder = 2 } });
        var q2 = AddQuestion(2, 2, "Project narrative", AnswerType.Textarea, true);
        q2.Question!.Rules.AddRange(new[] { new ScoringRule { Id = 201, QuestionId = 2, Family = RuleFamily.Presence, Points = 1, SortOrder = 1 }, new ScoringRule { Id = 202, QuestionId = 2, Family = RuleFamily.Presence, Points = 2, SortOrder = 2 } });
        var q3 = AddQuestion(3, 3, "Application method", AnswerType.Dropdown, true,
            new[] { new QuestionOption { Id = 31, QuestionId = 3, Label = "Online", Value = "online", SortOrder = 1 }, new QuestionOption { Id = 32, QuestionId = 3, Label = "Paper", Value = "paper", SortOrder = 2 } });
        var q4 = AddQuestion(4, 4, "Readiness", AnswerType.RadioGroup, true,
            new[] { new QuestionOption { Id = 41, QuestionId = 4, Label = "Ready", Value = "ready", SortOrder = 1 }, new QuestionOption { Id = 42, QuestionId = 4, Label = "Planning", Value = "planning", SortOrder = 2 } }, ScoringMode.FirstMatch);
        q4.Question!.Rules.AddRange(new[] { new ScoringRule { Id = 401, QuestionId = 4, Family = RuleFamily.Presence, Points = 3, SortOrder = 1 }, new ScoringRule { Id = 402, QuestionId = 4, Family = RuleFamily.Presence, Points = 4, SortOrder = 2 } });
        var q5 = AddQuestion(4, 5, "Support requested", AnswerType.Checkboxes, false,
            new[] { new QuestionOption { Id = 51, QuestionId = 5, Label = "Training", Value = "training", SortOrder = 1 }, new QuestionOption { Id = 52, QuestionId = 5, Label = "Equipment", Value = "equipment", SortOrder = 2 } }, sort: 2);
        var q6 = AddQuestion(5, 6, "Requested amount", AnswerType.Number, true);
        q6.Question!.MinValue = 0;
        q6.Question.MaxValue = 10;
        q6.Question.Rules.Add(new ScoringRule { Id = 601, QuestionId = 6, Family = RuleFamily.NumericRange, MinValue = 0, MaxValue = 5, Points = 5, SortOrder = 1 });
        var q7 = AddQuestion(5, 7, "Hidden branch detail", AnswerType.Textbox, true, sort: 2);
        var condition = new VisibilityCondition { Id = 77, ApplicationVersionId = versionEntity.Id, Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { Id = 770, VisibilityConditionId = 77, SourceQuestionId = 1, Operator = ConditionOperator.Unanswered } } };
        q7.VisibilityConditionId = condition.Id;

        categories.Add(new Category { Id = 1, Name = "Organization", SortOrder = 1, Elements = { q1 } });
        categories.Add(new Category { Id = 2, Name = "Narrative", SortOrder = 2, Elements = { q2 } });
        categories.Add(new Category { Id = 3, Name = "Method", SortOrder = 3, Elements = { q3 } });
        categories.Add(new Category { Id = 4, Name = "Readiness", SortOrder = 4, Elements = { q4, q5 } });
        categories.Add(new Category { Id = 5, Name = "Amount", SortOrder = 5, Elements = { q6, q7 } });
        versionEntity.Categories = categories;
        versionEntity.VisibilityConditions.Add(condition);
        return new ApplicationVersionGraph { Version = versionEntity, Categories = categories, VisibilityConditions = new[] { condition } };
    }
}

public sealed class PackageConsumerFactAttribute : FactAttribute
{
    public PackageConsumerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QAB_PACKAGE_HOST_URL")))
            Skip = "Run scripts/Test-Packages.ps1 to start the isolated package consumer before this test.";
    }
}
