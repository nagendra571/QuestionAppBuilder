using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Repositories;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class FollowUpTests
{
    [SqlServerFact]
    public async Task Branch_duplication_movement_category_copy_and_version_clone_remap_option_ownership()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(db));
        var app = await service.CreateApplicationAsync("Follow-up graph", 1);
        var category = await service.AddCategoryAsync(app.Id, "Organization");
        var root = await service.AddQuestionAsync(category.Id, "Organization type", AnswerType.RadioGroup);
        var other = await service.AddOptionAsync(root.Id, "Other", "other");
        var child = await service.AddFollowUpAsync(other.Id, "Partnership type", AnswerType.Dropdown);
        await service.AddVisibilityPredicateAsync(child.Id, root.Id, ConditionOperator.HasAnyOption, ConditionJunction.All, optionIds: [other.Id]);
        var nestedOption = await service.AddOptionAsync(child.Question!.Id, "Other partnership", "other-partnership");
        var nested = await service.AddFollowUpAsync(nestedOption.Id, "Years in partnership", AnswerType.Number);
        await service.AddQuestionAsync(category.Id, "Independent question", AnswerType.Textbox, false);
        db.ChangeTracker.Clear();
        var graph = await service.GetDraftGraphAsync(app.Id);
        var original = graph.Categories.Single().Elements.Single(e => e.Question?.Id == root.Id);
        var copy = await service.DuplicateElementAsync(original.Id);
        var copyBranch = FollowUpStructure.Branch(copy, (await service.GetDraftGraphAsync(app.Id)).Categories.Single().Elements);
        copyBranch.Should().HaveCount(3);
        copyBranch[1].ParentOptionId.Should().Be(copy.Question!.Options.Single().Id).And.NotBe(other.Id);
        copyBranch[2].ParentOptionId.Should().Be(copyBranch[1].Question!.Options.Single().Id).And.NotBe(nestedOption.Id);
        copyBranch[1].VisibilityCondition!.Predicates.Single().SourceQuestionId.Should().Be(copy.Question.Id);
        var categoryGraph = (await service.GetDraftGraphAsync(app.Id)).Categories.Single();
        var independent = categoryGraph.Elements.Single(e => e.Label == "Independent question");
        var targetIndex = FollowUpStructure.Ordered(categoryGraph.Elements).ToList().IndexOf(independent);
        await service.MoveElementAsync(original.Id, targetIndex);
        var moved = FollowUpStructure.Ordered((await service.GetDraftGraphAsync(app.Id)).Categories.Single().Elements);
        moved.TakeLast(3).Select(e => e.Id).Should().Equal(original.Id, child.Id, nested.Id);
        var copiedCategory = await service.DuplicateCategoryAsync(category.Id);
        foreach (var element in copiedCategory.Elements.Where(e => e.ParentOptionId is not null))
            FollowUpStructure.Parent(element, copiedCategory.Elements).Should().NotBeNull();
        await service.PublishAsync(app.Id);
        db.ChangeTracker.Clear();
        var nextVersion = await service.OpenDraftAsync(app.Id);
        var nextCategory = nextVersion.Categories.Single(c => c.Name == "Organization");
        foreach (var element in nextCategory.Elements.Where(e => e.ParentOptionId is not null))
        {
            FollowUpStructure.Parent(element, nextCategory.Elements).Should().NotBeNull();
            element.ParentOptionId.Should().NotBe(other.Id).And.NotBe(nestedOption.Id);
        }
        var published = await service.GetLatestPublishedGraphAsync(app.Id);
        published.Categories.Single(c => c.Id == category.Id).Elements.Single(e => e.Id == child.Id).ParentOptionId.Should().Be(other.Id);
    }

    [SqlServerFact]
    public Task Removing_option_deletes_nested_branch() => VerifyRemoval("option");
    [SqlServerFact]
    public Task Removing_question_deletes_nested_branch() => VerifyRemoval("question");
    [SqlServerFact]
    public Task Removing_category_deletes_nested_branch() => VerifyRemoval("category");

    private static async Task VerifyRemoval(string owner)
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(db));
        var app = await service.CreateApplicationAsync("Remove branch", 1);
        var category = await service.AddCategoryAsync(app.Id, "Branch");
        var root = await service.AddQuestionAsync(category.Id, "Choose", AnswerType.Checkboxes);
        var other = await service.AddOptionAsync(root.Id, "Other", "other");
        var child = await service.AddFollowUpAsync(other.Id, "More choices", AnswerType.RadioGroup);
        var childOption = await service.AddOptionAsync(child.Question!.Id, "Yes", "yes");
        var grandchild = await service.AddFollowUpAsync(childOption.Id, "Explain", AnswerType.Textarea);
        var rootElementId = (await service.GetDraftGraphAsync(app.Id)).Categories.Single().Elements.Single(e => e.Question?.Id == root.Id).Id;
        var retainedCategory = await service.AddCategoryAsync(app.Id, "Keep");
        var retained = await service.AddQuestionAsync(retainedCategory.Id, "Keep this", AnswerType.Textbox, false);
        db.ChangeTracker.Clear();
        if (owner == "option") await service.RemoveOptionAsync(other.Id);
        else if (owner == "question") await service.RemoveElementAsync(rootElementId);
        else await service.RemoveCategoryAsync(category.Id);
        db.ChangeTracker.Clear();
        (await db.Elements.AnyAsync(e => e.Id == child.Id || e.Id == grandchild.Id)).Should().BeFalse();
        (await db.Questions.AnyAsync(q => q.Id == retained.Id)).Should().BeTrue();
        (await db.QuestionOptions.AnyAsync(o => o.Id == other.Id)).Should().BeFalse();
    }
}
