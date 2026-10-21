using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public class FollowUpAuthoringTests : IDisposable
{
    private readonly EditorWebApplicationFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Follow_up_creation_enforces_draft_membership_revision_and_supported_types()
    {
        var ids = await _factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var app = await design.CreateApplicationAsync("Follow-up authoring", type.Id);
            var category = await design.AddCategoryAsync(app.Id, "Questions");
            var question = await design.AddQuestionAsync(category.Id, "Choose", AnswerType.Checkboxes);
            var option = await design.AddOptionAsync(question.Id, "Other", "other");
            var different = await design.CreateApplicationAsync("Different application", type.Id);
            var differentCategory = await design.AddCategoryAsync(different.Id, "Questions");
            var differentQuestion = await design.AddQuestionAsync(differentCategory.Id, "Choose", AnswerType.RadioGroup);
            var foreignOption = await design.AddOptionAsync(differentQuestion.Id, "Other", "other");
            return (App: app.Id, Option: option.Id, Foreign: foreignOption.Id);
        });
        var client = await _factory.CreateSignedInClientAsync("designer");
        client.DefaultRequestHeaders.Add("X-Requested-With", "QuestionAppBuilder");
        var (token, html) = await _factory.GetFormAsync(client, $"/ConfigureApp/Drafts/{ids.App}");
        var revision = WebUtility.HtmlDecode(Regex.Match(html, "name=\"ExpectedRevision\" value=\"([^\"]+)\"").Groups[1].Value);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["applicationId"] = ids.App.ToString(), ["expectedRevision"] = revision,
            ["parentOptionId"] = ids.Foreign.ToString(), ["followUpLabel"] = "Explain", ["followUpType"] = "Textarea", ["followUpRequired"] = "true"
        };
        var url = "/ConfigureApp/Structure/AddFollowUp";
        (await client.PostAsync(url, new FormUrlEncodedContent(form))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        form["parentOptionId"] = ids.Option.ToString();
        form["followUpType"] = "UnknownType";
        (await client.PostAsync(url, new FormUrlEncodedContent(form))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        form["followUpType"] = "Textarea";
        var created = await client.PostAsync(url, new FormUrlEncodedContent(form));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        (await client.PostAsync(url, new FormUrlEncodedContent(form))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _factory.WithScopeAsync(async services =>
        {
            var graph = (await services.GetRequiredService<IDraftService>().GetAsync(ids.App)).Definition;
            var child = graph.Categories.Single().Elements.Single(e => e.Label == "Explain");
            child.ParentOptionId.Should().Be(ids.Option);
            child.Question!.AnswerType.Should().Be(AnswerType.Textarea);
            child.Question.IsRequired.Should().BeTrue();
            return true;
        });
    }
}
