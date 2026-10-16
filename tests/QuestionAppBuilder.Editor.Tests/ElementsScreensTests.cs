using System.Globalization;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class ElementsScreensTests : IDisposable
{
    private readonly EditorWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Add_content_element_renders_in_category()
    {
        var (applicationId, categoryId) = await CreateApplicationWithCategoryAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddContentUrl(categoryId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Label"] = "Instructions",
                ["HelpText"] = "Provide your site licensing and capacity details.",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        var location = response.Headers.Location!.ToString();
        location.Should().Contain($"/Detail/{applicationId}");

        var body = await client.GetStringAsync(location);
        body.Should().Contain("Instructions");
        body.Should().Contain("Provide your site licensing and capacity details.");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var element = await database.Elements.SingleAsync(e => e.Label == "Instructions");
            var questionCount = await database.Questions.CountAsync(q => q.ElementId == element.Id);

            element.Kind.Should().Be(ElementKind.Content);
            questionCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Add_question_renders_with_answer_type_and_required_flag()
    {
        var (applicationId, categoryId) = await CreateApplicationWithCategoryAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddQuestionUrl(categoryId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Label"] = "Has the site received Early Start CT funding?",
                ["AnswerType"] = nameof(AnswerType.RadioGroup),
                ["IsRequired"] = "true",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("Has the site received Early Start CT funding?");
        body.Should().Contain("Radio Group");
        body.Should().Contain("Required</span>");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var element = await database.Elements.SingleAsync(e => e.Label == "Has the site received Early Start CT funding?");
            var question = await database.Questions.SingleAsync(q => q.ElementId == element.Id);

            element.Kind.Should().Be(ElementKind.Question);
            question.AnswerType.Should().Be(AnswerType.RadioGroup);
            question.IsRequired.Should().BeTrue();
            return true;
        });
    }

    [Fact]
    public async Task Add_option_to_choice_question_renders()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddOptionUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Label"] = "Previously funded",
                ["Value"] = "yes-prev",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("Previously funded");
        body.Should().Contain("yes-prev");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var option = await database.QuestionOptions.SingleAsync(o => o.QuestionId == questionId);

            option.Label.Should().Be("Previously funded");
            option.Value.Should().Be("yes-prev");
            return true;
        });
    }

    [Fact]
    public async Task Remove_element_removes_question_with_it()
    {
        var (applicationId, _, questionId, elementId) = await CreateQuestionAsync(AnswerType.RadioGroup);
        await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            await designService.AddOptionAsync(questionId, "Previously funded", "yes-prev");
            return true;
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            RemoveElementUrl(elementId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().NotContain("Licensed infant capacity");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var elementCount = await database.Elements.CountAsync(e => e.Id == elementId);
            var questionCount = await database.Questions.CountAsync(q => q.Id == questionId);

            elementCount.Should().Be(0);
            questionCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Remove_rule_referenced_option_is_friendly_error()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var optionId = await _factory.WithScopeAsync(async services =>
        {
            var designService = services.GetRequiredService<IApplicationDesignService>();
            var referenced = await designService.AddOptionAsync(questionId, "Previously funded", "yes-prev");
            await designService.AddOptionAsync(questionId, "First application", "no-first");
            await designService.AddRuleAsync(
                questionId,
                RuleFamily.Selection,
                5,
                MembershipOperator.AnyOf,
                new[] { referenced.Id });
            return referenced.Id;
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            RemoveOptionUrl(optionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("scoring rule");
        body.Should().Contain("Previously funded");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var optionCount = await database.QuestionOptions.CountAsync(o => o.Id == optionId);

            optionCount.Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task Add_option_to_non_choice_question_is_rejected_by_editor()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.Textbox);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddOptionUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Label"] = "Should not be persisted",
                ["Value"] = "nope",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("role=\"alert\"");
        body.Should().NotContain("Should not be persisted");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var optionCount = await database.QuestionOptions.CountAsync(o => o.QuestionId == questionId);

            optionCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Over_long_option_label_is_friendly()
    {
        var (applicationId, _, questionId, _) = await CreateQuestionAsync(AnswerType.RadioGroup);
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddOptionUrl(questionId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Label"] = new string('x', 201),
                ["Value"] = "overlong",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("at most 200");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var optionCount = await database.QuestionOptions.CountAsync(o => o.QuestionId == questionId);

            optionCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Unknown_answer_type_value_is_friendly_error()
    {
        var (applicationId, categoryId) = await CreateApplicationWithCategoryAsync();
        var client = await _factory.CreateSignedInClientAsync("designer");
        var (token, _) = await _factory.GetFormAsync(client, DetailUrl(applicationId));

        var response = await client.PostAsync(
            AddQuestionUrl(categoryId),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["applicationId"] = applicationId.ToString(CultureInfo.InvariantCulture),
                ["Label"] = "Should not be persisted",
                ["AnswerType"] = "999",
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var body = await client.GetStringAsync(response.Headers.Location!.ToString());
        body.Should().Contain("role=\"alert\"");

        await _factory.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
            var elementCount = await database.Elements.CountAsync(e => e.Label == "Should not be persisted");

            elementCount.Should().Be(0);
            return true;
        });
    }

    private static string DetailUrl(int applicationId)
        => $"/ConfigureApp/Applications/Detail/{applicationId}";

    private static string AddContentUrl(int categoryId)
        => $"/ConfigureApp/Elements/AddContent/{categoryId}";

    private static string AddQuestionUrl(int categoryId)
        => $"/ConfigureApp/Elements/AddQuestion/{categoryId}";

    private static string RemoveElementUrl(int elementId)
        => $"/ConfigureApp/Elements/Remove/{elementId}";

    private static string AddOptionUrl(int questionId)
        => $"/ConfigureApp/Options/Add/{questionId}";

    private static string RemoveOptionUrl(int optionId)
        => $"/ConfigureApp/Options/Remove/{optionId}";

    private async Task<(int ApplicationId, int CategoryId)> CreateApplicationWithCategoryAsync()
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = await SeedAndGetDesignServiceAsync(services);
            var application = await designService.CreateApplicationAsync("Endowment Expansion", await GetRfaTypeIdAsync(designService));
            var category = await designService.AddCategoryAsync(application.Id, "Site Information");
            return (application.Id, category.Id);
        });

    private async Task<(int ApplicationId, int CategoryId, int QuestionId, int ElementId)> CreateQuestionAsync(AnswerType answerType)
        => await _factory.WithScopeAsync(async services =>
        {
            var designService = await SeedAndGetDesignServiceAsync(services);
            var application = await designService.CreateApplicationAsync("Endowment Expansion", await GetRfaTypeIdAsync(designService));
            var category = await designService.AddCategoryAsync(application.Id, "Site Information");
            var question = await designService.AddQuestionAsync(category.Id, "Licensed infant capacity", answerType);
            return (application.Id, category.Id, question.Id, question.ElementId);
        });

    private static async Task<IApplicationDesignService> SeedAndGetDesignServiceAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<QuestionAppBuilderDbContext>();
        await database.Database.EnsureCreatedAsync();
        return services.GetRequiredService<IApplicationDesignService>();
    }

    private static async Task<int> GetRfaTypeIdAsync(IApplicationDesignService designService)
    {
        var types = await designService.ListApplicationTypesAsync();
        return types.Single(t => t.Name.Contains("RFA", StringComparison.Ordinal)).Id;
    }
}
