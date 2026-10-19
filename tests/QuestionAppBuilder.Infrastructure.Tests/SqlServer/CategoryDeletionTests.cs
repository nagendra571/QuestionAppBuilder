using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Repositories;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class CategoryDeletionTests
{
    [SqlServerFact]
    public async Task Removing_category_with_selection_rules_deletes_children_and_preserves_other_categories()
        => await VerifyDeletionAsync(removeCategory: true);

    [SqlServerFact]
    public async Task Removing_question_with_selection_rules_deletes_children_and_preserves_categories()
        => await VerifyDeletionAsync(removeCategory: false);

    [SqlServerFact]
    public async Task Removing_category_with_visibility_option_links_prunes_owned_graph()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var seeded = await SeedVisibilityGraphAsync(service, "Same category");
        context.ChangeTracker.Clear();

        await service.RemoveCategoryAsync(seeded.SourceCategoryId);

        context.ChangeTracker.Clear();
        (await context.Categories.AnyAsync(c => c.Id == seeded.SourceCategoryId)).Should().BeFalse();
        (await context.Questions.AnyAsync(q => q.Id == seeded.TargetQuestionId)).Should().BeFalse();
        (await context.VisibilityConditions.AnyAsync(c => c.Id == seeded.TargetConditionId)).Should().BeFalse();
        (await context.VisibilityPredicates.AnyAsync(p => p.Id == seeded.TargetPredicateId)).Should().BeFalse();
        (await context.VisibilityPredicateOptions.AnyAsync(o => o.QuestionOptionId == seeded.SourceOptionId)).Should().BeFalse();
        (await context.ScoringRules.AnyAsync(r => r.QuestionId == seeded.TargetQuestionId)).Should().BeFalse();
    }

    [SqlServerFact]
    public async Task Removing_source_question_prunes_only_dependent_conditions_and_keeps_other_graphs()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var app = await service.CreateApplicationAsync("Cross category visibility", 1);
        var sourceCategory = await service.AddCategoryAsync(app.Id, "Source");
        var targetCategory = await service.AddCategoryAsync(app.Id, "Target");
        var unrelatedCategory = await service.AddCategoryAsync(app.Id, "Unrelated");
        var source = await service.AddQuestionAsync(sourceCategory.Id, "Source dropdown", AnswerType.Dropdown);
        var yes = await service.AddOptionAsync(source.Id, "Yes", "yes");
        var target = await service.AddQuestionAsync(targetCategory.Id, "Target checkboxes", AnswerType.Checkboxes);
        var alpha = await service.AddOptionAsync(target.Id, "Alpha", "alpha");
        await service.AddOptionAsync(target.Id, "Beta", "beta");
        var rule = await service.AddRuleAsync(target.Id, RuleFamily.Selection, 3, MembershipOperator.AnyOf, new[] { alpha.Id });
        var targetElement = await context.Elements.SingleAsync(e => e.Question!.Id == target.Id);
        var targetCondition = await service.AddVisibilityPredicateAsync(targetElement.Id, source.Id,
            ConditionOperator.HasAnyOption, ConditionJunction.All, optionIds: new[] { yes.Id });

        var unrelatedSource = await service.AddQuestionAsync(unrelatedCategory.Id, "Unrelated source", AnswerType.Textbox);
        var laterCategory = await service.AddCategoryAsync(app.Id, "Later");
        var unrelatedTarget = await service.AddQuestionAsync(laterCategory.Id, "Unrelated target", AnswerType.Textbox);
        var unrelatedElement = await context.Elements.SingleAsync(e => e.Question!.Id == unrelatedTarget.Id);
        var unrelatedCondition = await service.AddVisibilityPredicateAsync(unrelatedElement.Id, unrelatedSource.Id,
            ConditionOperator.Answered, ConditionJunction.All);
        context.ChangeTracker.Clear();

        await service.RemoveElementAsync(await context.Elements.Where(e => e.Question!.Id == source.Id).Select(e => e.Id).SingleAsync());

        context.ChangeTracker.Clear();
        (await context.Elements.AnyAsync(e => e.Question!.Id == source.Id)).Should().BeFalse();
        var retainedTarget = await context.Elements.Include(e => e.Question).SingleAsync(e => e.Question!.Id == target.Id);
        retainedTarget.VisibilityConditionId.Should().BeNull();
        (await context.ScoringRules.AnyAsync(r => r.Id == rule.Id)).Should().BeTrue();
        (await context.QuestionOptions.CountAsync(o => o.QuestionId == target.Id)).Should().Be(2);
        (await context.VisibilityConditions.AnyAsync(c => c.Id == targetCondition.Id)).Should().BeFalse();
        (await context.VisibilityConditions.AnyAsync(c => c.Id == unrelatedCondition.Id)).Should().BeTrue();
        (await context.Elements.SingleAsync(e => e.Question!.Id == unrelatedTarget.Id)).VisibilityConditionId.Should().Be(unrelatedCondition.Id);
    }

    [SqlServerFact]
    public async Task Removing_referenced_option_prunes_condition_but_keeps_remaining_question_and_rule()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var seeded = await SeedVisibilityGraphAsync(service, "Option removal");
        context.ChangeTracker.Clear();

        await service.RemoveOptionAsync(seeded.SourceOptionId);

        context.ChangeTracker.Clear();
        (await context.QuestionOptions.AnyAsync(o => o.Id == seeded.SourceOptionId)).Should().BeFalse();
        (await context.QuestionOptions.CountAsync(o => o.QuestionId == seeded.SourceQuestionId)).Should().Be(1);
        (await context.ScoringRules.AnyAsync(r => r.Id == seeded.SelectionRuleId)).Should().BeTrue();
        (await context.VisibilityConditions.AnyAsync(c => c.Id == seeded.TargetConditionId)).Should().BeFalse();
        (await context.Elements.SingleAsync(e => e.Question!.Id == seeded.TargetQuestionId)).VisibilityConditionId.Should().BeNull();
    }

    [SqlServerFact]
    public async Task Removing_source_question_with_answered_condition_prunes_predicate()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var app = await service.CreateApplicationAsync("Answered source", 1);
        var category = await service.AddCategoryAsync(app.Id, "Category");
        var source = await service.AddQuestionAsync(category.Id, "Source", AnswerType.Textbox);
        var target = await service.AddQuestionAsync(category.Id, "Target", AnswerType.Textbox);
        var targetElement = await context.Elements.SingleAsync(e => e.Question!.Id == target.Id);
        var condition = await service.AddVisibilityPredicateAsync(targetElement.Id, source.Id, ConditionOperator.Answered, ConditionJunction.All);
        var predicateId = await context.VisibilityPredicates.Where(p => p.VisibilityConditionId == condition.Id).Select(p => p.Id).SingleAsync();
        var sourceElementId = await context.Elements.Where(e => e.Question!.Id == source.Id).Select(e => e.Id).SingleAsync();
        context.ChangeTracker.Clear();

        await service.RemoveElementAsync(sourceElementId);

        context.ChangeTracker.Clear();
        (await context.VisibilityConditions.AnyAsync(c => c.Id == condition.Id)).Should().BeFalse();
        (await context.VisibilityPredicates.AnyAsync(p => p.Id == predicateId)).Should().BeFalse();
        (await context.Elements.SingleAsync(e => e.Question!.Id == target.Id)).VisibilityConditionId.Should().BeNull();
    }

    [SqlServerFact]
    public async Task Published_graph_remains_unchanged_when_draft_category_is_removed()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var seeded = await SeedVisibilityGraphAsync(service, "Published preservation");
        await service.PublishAsync(seeded.ApplicationId);
        context.ChangeTracker.Clear();
        var before = await PublishedSnapshotAsync(context, seeded.ApplicationId);
        await service.OpenDraftAsync(seeded.ApplicationId);
        context.ChangeTracker.Clear();
        var draftCategoryId = await (from category in context.Categories
                                     join version in context.ApplicationVersions on category.ApplicationVersionId equals version.Id
                                     where version.ApplicationId == seeded.ApplicationId && version.Status == VersionStatus.Draft
                                     select category.Id).SingleAsync();

        await service.RemoveCategoryAsync(draftCategoryId);

        context.ChangeTracker.Clear();
        (await PublishedSnapshotAsync(context, seeded.ApplicationId)).Should().BeEquivalentTo(before);
        (await context.Categories.AnyAsync(c => c.Id == draftCategoryId)).Should().BeFalse();
    }

    [SqlServerFact]
    public async Task Visibility_reference_foreign_keys_remain_no_action_and_reject_direct_owner_deletion()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var seeded = await SeedVisibilityGraphAsync(service, "Reference guard");
        context.ChangeTracker.Clear();

        var actions = await context.Database.SqlQueryRaw<string>("""
            SELECT delete_referential_action_desc AS [Value]
            FROM sys.foreign_keys
            WHERE name IN ('FK_VisibilityPredicateOptions_QuestionOptions_QuestionOptionId',
                           'FK_VisibilityPredicates_Questions_SourceQuestionId')
            ORDER BY name
            """).ToListAsync();
        actions.Should().HaveCount(2).And.OnlyContain(action => action == "NO_ACTION");

        await using (var optionContext = database.CreateContext())
        {
            optionContext.QuestionOptions.Remove(await optionContext.QuestionOptions.SingleAsync(o => o.Id == seeded.SourceOptionId));
            Func<Task> deleteOption = () => optionContext.SaveChangesAsync();
            await deleteOption.Should().ThrowAsync<DbUpdateException>();
        }
        await using (var questionContext = database.CreateContext())
        {
            questionContext.Questions.Remove(await questionContext.Questions.SingleAsync(q => q.Id == seeded.SourceQuestionId));
            Func<Task> deleteQuestion = () => questionContext.SaveChangesAsync();
            await deleteQuestion.Should().ThrowAsync<DbUpdateException>();
        }
    }

    private static async Task VerifyDeletionAsync(bool removeCategory)
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new ApplicationDesignService(new ApplicationRepository(context));
        var app = await service.CreateApplicationAsync("Deletion regression", 1);
        var category = await service.AddCategoryAsync(app.Id, "Remove me");
        var retained = await service.AddCategoryAsync(app.Id, "Keep me");
        var element = new Element { Kind = ElementKind.Question, Label = "Choose", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Checkboxes } };
        var option = new QuestionOption { Label = "Yes", Value = "yes", SortOrder = 1 };
        element.Question.Options.Add(option);
        element.Question.Rules.Add(new ScoringRule { Family = RuleFamily.Selection, Points = 2,
            MembershipOperator = MembershipOperator.AnyOf,
            Options = { new ScoringRuleOption { QuestionOption = option } } });
        category.Elements.Add(element);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Direct deletion must still be rejected by SQL Server when a rule references the option.
        await using (var guardContext = database.CreateContext())
        {
            guardContext.QuestionOptions.Remove(await guardContext.QuestionOptions.SingleAsync());
            Func<Task> removeReferencedOption = () => guardContext.SaveChangesAsync();
            await removeReferencedOption.Should().ThrowAsync<DbUpdateException>();
        }

        if (removeCategory) await service.RemoveCategoryAsync(category.Id);
        else await service.RemoveElementAsync(element.Id);
        context.ChangeTracker.Clear();

        (await context.Categories.Select(c => c.Id).ToListAsync()).Should()
            .BeEquivalentTo(removeCategory ? new[] { retained.Id } : new[] { category.Id, retained.Id });
        (await context.Elements.CountAsync()).Should().Be(0);
        (await context.Questions.CountAsync()).Should().Be(0);
        (await context.QuestionOptions.CountAsync()).Should().Be(0);
        (await context.ScoringRules.CountAsync()).Should().Be(0);
        (await context.ScoringRuleOptions.CountAsync()).Should().Be(0);
    }

    private static async Task<VisibilityGraphIds> SeedVisibilityGraphAsync(ApplicationDesignService service, string name)
    {
        var app = await service.CreateApplicationAsync(name, 1);
        var category = await service.AddCategoryAsync(app.Id, "Source and target");
        var source = await service.AddQuestionAsync(category.Id, "Source dropdown", AnswerType.Dropdown);
        var yes = await service.AddOptionAsync(source.Id, "Yes", "yes");
        await service.AddOptionAsync(source.Id, "No", "no");
        var target = await service.AddQuestionAsync(category.Id, "Target checkboxes", AnswerType.Checkboxes);
        var alpha = await service.AddOptionAsync(target.Id, "Alpha", "alpha");
        await service.AddOptionAsync(target.Id, "Beta", "beta");
        var rule = await service.AddRuleAsync(target.Id, RuleFamily.Selection, 4, MembershipOperator.AnyOf, new[] { alpha.Id });
        var elementId = await service.GetDraftGraphAsync(app.Id).ContinueWith(t => t.Result.Categories.SelectMany(c => c.Elements)
            .Single(e => e.Question?.Id == target.Id).Id);
        var condition = await service.AddVisibilityPredicateAsync(elementId, source.Id, ConditionOperator.HasAnyOption,
            ConditionJunction.All, optionIds: new[] { yes.Id });
        var predicateId = condition.Predicates.Single().Id;
        return new VisibilityGraphIds(app.Id, category.Id, source.Id, yes.Id, target.Id, rule.Id, condition.Id, predicateId);
    }

    private static async Task<PublishedGraphSnapshot> PublishedSnapshotAsync(QuestionAppBuilderDbContext context, int appId)
    {
        var versionId = await context.Applications.Where(a => a.Id == appId).Select(a => a.CurrentVersionId!.Value).SingleAsync();
        var categories = await context.Categories.Where(c => c.ApplicationVersionId == versionId)
            .OrderBy(c => c.Id).Select(c => new { c.Id, c.Name }).ToListAsync();
        var questions = await (from question in context.Questions
                               join element in context.Elements on question.ElementId equals element.Id
                               join category in context.Categories on element.CategoryId equals category.Id
                               where category.ApplicationVersionId == versionId
                               select question)
            .OrderBy(q => q.Id).Select(q => new { q.Id, q.AnswerType }).ToListAsync();
        var options = await (from option in context.QuestionOptions
                             join question in context.Questions on option.QuestionId equals question.Id
                             join element in context.Elements on question.ElementId equals element.Id
                             join category in context.Categories on element.CategoryId equals category.Id
                             where category.ApplicationVersionId == versionId
                             select option)
            .OrderBy(o => o.Id).Select(o => new { o.Id, o.Label, o.Value }).ToListAsync();
        var conditions = await context.VisibilityConditions.Where(c => c.ApplicationVersionId == versionId)
            .OrderBy(c => c.Id).Select(c => new { c.Id, c.Junction }).ToListAsync();
        var predicates = await (from predicate in context.VisibilityPredicates
                                join condition in context.VisibilityConditions on predicate.VisibilityConditionId equals condition.Id
                                where condition.ApplicationVersionId == versionId
                                select predicate)
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.SourceQuestionId, p.Operator }).ToListAsync();
        var rules = await (from rule in context.ScoringRules
                           join question in context.Questions on rule.QuestionId equals question.Id
                           join element in context.Elements on question.ElementId equals element.Id
                           join category in context.Categories on element.CategoryId equals category.Id
                           where category.ApplicationVersionId == versionId
                           select rule)
            .OrderBy(r => r.Id).Select(r => new { r.Id, r.QuestionId, r.Family, r.Points }).ToListAsync();
        var ruleOptions = await (from link in context.ScoringRuleOptions
                                 join rule in context.ScoringRules on link.ScoringRuleId equals rule.Id
                                 join question in context.Questions on rule.QuestionId equals question.Id
                                 join element in context.Elements on question.ElementId equals element.Id
                                 join category in context.Categories on element.CategoryId equals category.Id
                                 where category.ApplicationVersionId == versionId
                                 select link)
            .OrderBy(link => link.Id).Select(link => $"{link.Id}:{link.ScoringRuleId}:{link.QuestionOptionId}").ToArrayAsync();
        return new PublishedGraphSnapshot(categories.Select(x => $"{x.Id}:{x.Name}").ToArray(),
            questions.Select(x => $"{x.Id}:{x.AnswerType}").ToArray(), options.Select(x => $"{x.Id}:{x.Label}:{x.Value}").ToArray(),
            conditions.Select(x => $"{x.Id}:{x.Junction}").ToArray(), predicates.Select(x => $"{x.Id}:{x.SourceQuestionId}:{x.Operator}").ToArray(),
            rules.Select(x => $"{x.Id}:{x.QuestionId}:{x.Family}:{x.Points}").ToArray(), ruleOptions);
    }

    private sealed record VisibilityGraphIds(int ApplicationId, int SourceCategoryId, int SourceQuestionId, int SourceOptionId,
        int TargetQuestionId, int SelectionRuleId, int TargetConditionId, int TargetPredicateId);

    private sealed record PublishedGraphSnapshot(string[] Categories, string[] Questions, string[] Options,
        string[] Conditions, string[] Predicates, string[] Rules, string[] RuleOptions);
}
