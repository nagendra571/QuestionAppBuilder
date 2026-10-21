using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class QuestionnaireOverviewTests
{
    [Fact]
    public async Task Detail_counts_questions_separately_and_numbers_items_in_display_order_after_reordering()
    {
        using var factory = new EditorWebApplicationFactory();
        var ids = await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<QuestionAppBuilderDbContext>().Database.EnsureCreatedAsync();
            var design = services.GetRequiredService<IApplicationDesignService>();
            var type = (await design.ListApplicationTypesAsync()).First();
            var app = await design.CreateApplicationAsync("Numbered questionnaire", type.Id);
            var first = await design.AddCategoryAsync(app.Id, "First category");
            var second = await design.AddCategoryAsync(app.Id, "Second category");
            await design.AddContentElementAsync(first.Id, "First content", null);
            await design.AddQuestionAsync(first.Id, "First question", AnswerType.Textbox, false);
            await design.AddQuestionAsync(second.Id, "Second question", AnswerType.Number, false);
            await design.AddContentElementAsync(second.Id, "Second content", null);
            return (Application: app.Id, SecondCategory: second.Id);
        });
        var client = await factory.CreateSignedInClientAsync("designer");
        var html = await client.GetStringAsync($"/ConfigureApp/Applications/Detail/{ids.Application}");
        var summary = Regex.Match(html, "<p class=\"cv-questionnaire-summary\".*?</p>", RegexOptions.Singleline).Value;
        summary.Should().Contain("2 questions").And.Contain("across 2 categories").And.Contain("2 content blocks");
        NumberFor(html, "First content").Should().Be("1.1");
        NumberFor(html, "First question").Should().Be("1.2");
        NumberFor(html, "Second question").Should().Be("2.1");
        NumberFor(html, "Second content").Should().Be("2.2");
        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IApplicationDesignService>().MoveCategoryAsync(ids.SecondCategory, 0);
            return true;
        });
        var reordered = await client.GetStringAsync($"/ConfigureApp/Applications/Detail/{ids.Application}");
        NumberFor(reordered, "Second question").Should().Be("1.1");
        NumberFor(reordered, "First question").Should().Be("2.2");
    }

    private static string NumberFor(string html, string label) => Regex.Match(html,
        "class=\"cv-element-number\">([^<]+)</span><span>" + Regex.Escape(label)).Groups[1].Value;
}
