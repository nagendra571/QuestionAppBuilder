using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Repositories;

namespace QuestionAppBuilder.Application.Tests.Services;

public class ApplicationDesignServiceTests
{
    [Fact]
    public async Task CreateApplication_starts_draft_version_one()
    {
        using var ctx = new TestContext();

        var application = await ctx.Service.CreateApplicationAsync("Grant Review", ctx.ApplicationTypeId);

        application.Id.Should().BePositive();
        application.Name.Should().Be("Grant Review");
        application.ApplicationTypeId.Should().Be(ctx.ApplicationTypeId);

        var stored = (await ctx.Service.ListApplicationsAsync()).Single(app => app.Id == application.Id);
        stored.Name.Should().Be("Grant Review");
        stored.ApplicationTypeId.Should().Be(ctx.ApplicationTypeId);

        var graph = await ctx.Service.GetDraftGraphAsync(application.Id);
        graph.Version.VersionNumber.Should().Be(1);
        graph.Version.Status.Should().Be(VersionStatus.Draft);
    }

    [Fact]
    public async Task First_publish_enables_response_starts_and_submissions()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Availability defaults", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "General");
        await ctx.Service.AddQuestionAsync(category.Id, "Name", AnswerType.Textbox);

        await ctx.Service.PublishAsync(application.Id);

        var published = await ctx.Service.GetApplicationAsync(application.Id);
        published.AcceptNewResponses.Should().BeTrue();
        published.AcceptSubmissions.Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_name_is_rejected()
    {
        using var ctx = new TestContext();
        await ctx.Service.CreateApplicationAsync("Duplicate Me", ctx.ApplicationTypeId);

        Func<Task> act = () => ctx.Service.CreateApplicationAsync("Duplicate Me", ctx.ApplicationTypeId);

        await act.Should().ThrowAsync<DuplicateApplicationNameException>();
    }

    [Fact]
    public async Task Add_category_element_question_option_rule_builds_the_draft()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Chain", ctx.ApplicationTypeId);

        var category = await ctx.Service.AddCategoryAsync(application.Id, "Eligibility");
        var content = await ctx.Service.AddContentElementAsync(category.Id, "Read carefully", "Scoring starts below.");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Are you registered?", AnswerType.Dropdown);
        var option = await ctx.Service.AddOptionAsync(question.Id, "Yes", "yes");
        var rule = await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Selection, 5, MembershipOperator.AnyOf, new[] { option.Id });

        category.Id.Should().BePositive();
        content.SortOrder.Should().Be(1);
        question.Id.Should().BePositive();
        option.SortOrder.Should().Be(1);
        rule.Id.Should().BePositive();

        var graph = await ctx.Service.GetDraftGraphAsync(application.Id);

        graph.Version.VersionNumber.Should().Be(1);
        var graphCategory = graph.Categories.Should().ContainSingle().Subject;
        graphCategory.Id.Should().Be(category.Id);
        graphCategory.Name.Should().Be("Eligibility");
        graphCategory.SortOrder.Should().Be(1);

        graphCategory.Elements.Should().HaveCount(2);
        var contentElement = graphCategory.Elements.Single(e => e.Kind == ElementKind.Content);
        contentElement.Id.Should().Be(content.Id);
        contentElement.Label.Should().Be("Read carefully");
        contentElement.HelpText.Should().Be("Scoring starts below.");
        contentElement.Question.Should().BeNull();

        var questionElement = graphCategory.Elements.Single(e => e.Kind == ElementKind.Question);
        questionElement.Id.Should().Be(question.ElementId);
        questionElement.SortOrder.Should().Be(2);
        questionElement.Question.Should().NotBeNull();
        var graphQuestion = questionElement.Question!;
        graphQuestion.Id.Should().Be(question.Id);
        graphQuestion.AnswerType.Should().Be(AnswerType.Dropdown);
        graphQuestion.IsRequired.Should().BeTrue();
        graphQuestion.Options.Should().ContainSingle().Which.Value.Should().Be("yes");
        var graphRule = graphQuestion.Rules.Should().ContainSingle().Subject;
        graphRule.Id.Should().Be(rule.Id);
        graphRule.Family.Should().Be(RuleFamily.Selection);
        graphRule.Points.Should().Be(5);
        graphRule.MembershipOperator.Should().Be(MembershipOperator.AnyOf);
        graphRule.Options.Should().ContainSingle().Which.QuestionOptionId.Should().Be(option.Id);
    }

    [Fact]
    public async Task Out_of_range_label_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Boundaries", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Basics");
        var tooLong = new string('x', ValidationLimits.LabelMaxLength + 1);

        Func<Task> act = () => ctx.Service.AddQuestionAsync(category.Id, tooLong, AnswerType.Textbox);

        await act.Should().ThrowAsync<DefinitionValidationException>();

        var graph = await ctx.Service.GetDraftGraphAsync(application.Id);
        graph.Categories.Single().Elements.Should().BeEmpty();
    }

    [Fact]
    public async Task Selection_rule_with_option_from_another_question_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Cross rules", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var firstQuestion = await ctx.Service.AddQuestionAsync(category.Id, "First question", AnswerType.RadioGroup);
        var secondQuestion = await ctx.Service.AddQuestionAsync(category.Id, "Second question", AnswerType.RadioGroup);
        var firstOption = await ctx.Service.AddOptionAsync(firstQuestion.Id, "First", "first");
        var foreignOption = await ctx.Service.AddOptionAsync(secondQuestion.Id, "Second", "second");

        Func<Task> act = () => ctx.Service.AddRuleAsync(
            firstQuestion.Id, RuleFamily.Selection, 3, MembershipOperator.AnyOf, new[] { foreignOption.Id });

        await act.Should().ThrowAsync<DefinitionValidationException>();

        var rule = await ctx.Service.AddRuleAsync(
            firstQuestion.Id, RuleFamily.Selection, 3, MembershipOperator.AnyOf, new[] { firstOption.Id });

        rule.Id.Should().BePositive();
        rule.QuestionId.Should().Be(firstQuestion.Id);
    }

    [Fact]
    public async Task Publish_sets_status_and_current_version()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Publishable", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Basics");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Scorable?", AnswerType.Textbox);
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        var versionId = (await ctx.Service.GetDraftGraphAsync(application.Id)).Version.Id;

        await ctx.Service.PublishAsync(application.Id);

        var published = await ctx.Repository.GetVersionGraphAsync(versionId);
        published.Version.Status.Should().Be(VersionStatus.Published);
        published.Version.PublishedUtc.Should().NotBeNull();

        var stored = await ctx.Repository.GetAsync(application.Id);
        stored.Should().NotBeNull();
        stored!.CurrentVersionId.Should().Be(versionId);
    }

    [Fact]
    public async Task Publish_requires_a_question()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Questionless", ctx.ApplicationTypeId);
        await ctx.Service.AddCategoryAsync(application.Id, "Only category");

        Func<Task> act = () => ctx.Service.PublishAsync(application.Id);

        await act.Should().ThrowAsync<DefinitionValidationException>();
    }

    [Fact]
    public async Task Publish_with_no_draft_throws()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Twice", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Basics");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Scorable?", AnswerType.Textbox);
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        await ctx.Service.PublishAsync(application.Id);

        Func<Task> act = () => ctx.Service.PublishAsync(application.Id);

        await act.Should().ThrowAsync<DefinitionValidationException>().WithMessage("No draft to publish.");
    }

    [Fact]
    public async Task Edit_after_publish_clones_a_new_draft()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Versioned", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Original");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "What is your name?", AnswerType.Textbox);
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        var draftGraph = await ctx.Service.GetDraftGraphAsync(application.Id);
        var publishedVersionId = draftGraph.Version.Id;
        draftGraph.Version.Status = VersionStatus.Published;
        draftGraph.Version.PublishedUtc = DateTime.UtcNow;
        application.CurrentVersionId = publishedVersionId;
        await ctx.Context.SaveChangesAsync();

        var newCategory = await ctx.Service.AddCategoryAsync(application.Id, "Added after publish");

        var newDraft = await ctx.Service.GetDraftGraphAsync(application.Id);
        newDraft.Version.VersionNumber.Should().Be(2);
        newDraft.Version.Status.Should().Be(VersionStatus.Draft);
        newDraft.Categories.Select(c => c.Name).Should().BeEquivalentTo(new[] { "Original", "Added after publish" });
        newCategory.Id.Should().BePositive();

        var stillPublished = await ctx.Repository.GetVersionGraphAsync(publishedVersionId);
        stillPublished.Version.Status.Should().Be(VersionStatus.Published);
        stillPublished.Categories.Should().HaveCount(1);
        stillPublished.Categories.Select(c => c.Name).Should().BeEquivalentTo(new[] { "Original" });
    }

    [Fact]
    public async Task Clone_via_child_id_relinks_rule_options()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Relink", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Pick all that apply", AnswerType.Checkboxes);
        var firstOption = await ctx.Service.AddOptionAsync(question.Id, "First", "first");
        await ctx.Service.AddOptionAsync(question.Id, "Second", "second");
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Selection, 4, MembershipOperator.AnyOf, new[] { firstOption.Id });

        var publishedVersionId = (await ctx.Service.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Service.PublishAsync(application.Id);

        await ctx.Service.UpdateCategoryAsync(category.Id, "renamed");

        var draft = await ctx.Service.GetDraftGraphAsync(application.Id);
        draft.Version.VersionNumber.Should().Be(2);
        draft.Version.Status.Should().Be(VersionStatus.Draft);

        var clonedCategory = draft.Categories.Should().ContainSingle().Subject;
        clonedCategory.Id.Should().NotBe(category.Id);
        clonedCategory.Name.Should().Be("renamed");

        var clonedQuestion = clonedCategory.Elements.Select(e => e.Question).Single(q => q is not null)!;
        clonedQuestion.Options.Select(o => o.Value).Should().BeEquivalentTo(new[] { "first", "second" });
        var clonedFirst = clonedQuestion.Options.Single(o => o.Value == "first");
        clonedFirst.Id.Should().NotBe(firstOption.Id);
        clonedFirst.Label.Should().Be("First");
        clonedFirst.Value.Should().Be("first");

        var clonedRule = clonedQuestion.Rules.Should().ContainSingle().Subject;
        clonedRule.Options.Should().ContainSingle().Which.QuestionOptionId.Should().Be(clonedFirst.Id);

        using var freshContext = new QuestionAppBuilderDbContext(ctx.Options);
        var published = await new ApplicationRepository(freshContext).GetVersionGraphAsync(publishedVersionId);
        published.Version.Status.Should().Be(VersionStatus.Published);
        var publishedQuestion = published.Categories.SelectMany(c => c.Elements).Select(e => e.Question).Single(q => q is not null)!;
        publishedQuestion.Options.Should().HaveCount(2);
        var publishedFirst = publishedQuestion.Options.Single(o => o.Value == "first");
        publishedFirst.Id.Should().Be(firstOption.Id);
        publishedFirst.Label.Should().Be("First");
        publishedQuestion.Rules.Should().ContainSingle().Which.Options.Should().ContainSingle()
            .Which.QuestionOptionId.Should().Be(firstOption.Id);
    }

    [Fact]
    public async Task Selection_rule_without_membership_operator_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Operatorless", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Pick one", AnswerType.Dropdown);
        var option = await ctx.Service.AddOptionAsync(question.Id, "Yes", "yes");

        Func<Task> act = () => ctx.Service.AddRuleAsync(
            question.Id, RuleFamily.Selection, 5, membershipOperator: null, optionIds: new[] { option.Id });

        await act.Should().ThrowAsync<DefinitionValidationException>();

        var graph = await ctx.Service.GetDraftGraphAsync(application.Id);
        var storedQuestion = graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question).Single(q => q is not null)!;
        storedQuestion.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task Numeric_range_rule_with_min_above_max_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Bad range", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "How many?", AnswerType.Number);

        Func<Task> act = () => ctx.Service.AddRuleAsync(
            question.Id, RuleFamily.NumericRange, 3, minValue: 10m, maxValue: 5m);

        await act.Should().ThrowAsync<DefinitionValidationException>();

        var graph = await ctx.Service.GetDraftGraphAsync(application.Id);
        var storedQuestion = graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question).Single(q => q is not null)!;
        storedQuestion.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_required_string_arguments_are_rejected_with_argument_exception()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Null guards", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Basics");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Name", AnswerType.Textbox);

        Func<Task> create = () => ctx.Service.CreateApplicationAsync(null!, ctx.ApplicationTypeId);
        Func<Task> addCategory = () => ctx.Service.AddCategoryAsync(application.Id, null!);
        Func<Task> updateCategory = () => ctx.Service.UpdateCategoryAsync(category.Id, null!);
        Func<Task> addContent = () => ctx.Service.AddContentElementAsync(category.Id, null!);
        Func<Task> addQuestion = () => ctx.Service.AddQuestionAsync(category.Id, null!, AnswerType.Textbox);
        Func<Task> addOptionLabel = () => ctx.Service.AddOptionAsync(question.Id, null!, "value");
        Func<Task> addOptionValue = () => ctx.Service.AddOptionAsync(question.Id, "Label", null!);

        await create.Should().ThrowAsync<ArgumentException>();
        await addCategory.Should().ThrowAsync<ArgumentException>();
        await updateCategory.Should().ThrowAsync<ArgumentException>();
        await addContent.Should().ThrowAsync<ArgumentException>();
        await addQuestion.Should().ThrowAsync<ArgumentException>();
        await addOptionLabel.Should().ThrowAsync<ArgumentException>();
        await addOptionValue.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Duplicate_rule_option_ids_are_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Double option", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Pick all that apply", AnswerType.Checkboxes);
        var option = await ctx.Service.AddOptionAsync(question.Id, "First", "first");

        Func<Task> act = () => ctx.Service.AddRuleAsync(
            question.Id, RuleFamily.Selection, 4, MembershipOperator.AnyOf, new[] { option.Id, option.Id });

        await act.Should().ThrowAsync<DefinitionValidationException>();

        var graph = await ctx.Service.GetDraftGraphAsync(application.Id);
        var storedQuestion = graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question).Single(q => q is not null)!;
        storedQuestion.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_rule_to_published_question_clones_with_relinked_options()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Post-publish rules", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Pick all that apply", AnswerType.Checkboxes);
        var firstOption = await ctx.Service.AddOptionAsync(question.Id, "First", "first");
        var secondOption = await ctx.Service.AddOptionAsync(question.Id, "Second", "second");
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        var publishedVersionId = (await ctx.Service.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Service.PublishAsync(application.Id);

        var rule = await ctx.Service.AddRuleAsync(
            question.Id, RuleFamily.Selection, 5, MembershipOperator.AnyOf, new[] { firstOption.Id, secondOption.Id });

        var draft = await ctx.Service.GetDraftGraphAsync(application.Id);
        draft.Version.VersionNumber.Should().Be(2);
        draft.Version.Status.Should().Be(VersionStatus.Draft);

        var clonedQuestion = draft.Categories.SelectMany(c => c.Elements).Select(e => e.Question).Single(q => q is not null)!;
        clonedQuestion.Id.Should().NotBe(question.Id);
        var clonedFirst = clonedQuestion.Options.Single(o => o.Value == "first");
        var clonedSecond = clonedQuestion.Options.Single(o => o.Value == "second");
        clonedFirst.Id.Should().NotBe(firstOption.Id);
        clonedSecond.Id.Should().NotBe(secondOption.Id);
        clonedFirst.Label.Should().Be("First");

        var clonedRule = clonedQuestion.Rules.Single(r => r.Id == rule.Id);
        clonedRule.Family.Should().Be(RuleFamily.Selection);
        clonedRule.Points.Should().Be(5);
        clonedRule.MembershipOperator.Should().Be(MembershipOperator.AnyOf);
        clonedRule.Options.Select(o => o.QuestionOptionId).Should().BeEquivalentTo(new[] { clonedFirst.Id, clonedSecond.Id });

        using var freshContext = new QuestionAppBuilderDbContext(ctx.Options);
        var published = await new ApplicationRepository(freshContext).GetVersionGraphAsync(publishedVersionId);
        published.Version.Status.Should().Be(VersionStatus.Published);
        var publishedQuestion = published.Categories.SelectMany(c => c.Elements).Select(e => e.Question).Single(q => q is not null)!;
        publishedQuestion.Id.Should().Be(question.Id);
        publishedQuestion.Options.Select(o => o.Id).Should().BeEquivalentTo(new[] { firstOption.Id, secondOption.Id });
        var publishedRule = publishedQuestion.Rules.Should().ContainSingle().Subject;
        publishedRule.Family.Should().Be(RuleFamily.Presence);
        publishedRule.Points.Should().Be(1);
        publishedRule.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task TryGetDraftGraph_returns_null_after_publish()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Published only", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Basics");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Scorable?", AnswerType.Textbox);
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);
        await ctx.Service.PublishAsync(application.Id);

        var draft = await ctx.Service.TryGetDraftGraphAsync(application.Id);

        draft.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestPublishedGraph_returns_published_content()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Published read", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Published category");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Scorable?", AnswerType.Textbox);
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);
        await ctx.Service.PublishAsync(application.Id);

        var graph = await ctx.Service.GetLatestPublishedGraphAsync(application.Id);

        graph.Version.Status.Should().Be(VersionStatus.Published);
        graph.Categories.Should().ContainSingle().Which.Name.Should().Be("Published category");
    }

    [Fact]
    public async Task GetLatestPublishedGraph_throws_when_never_published()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Draft only", ctx.ApplicationTypeId);

        Func<Task> act = () => ctx.Service.GetLatestPublishedGraphAsync(application.Id);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task OpenDraft_clones_published_once()
    {
        using var ctx = new TestContext();
        var application = await ctx.Service.CreateApplicationAsync("Open draft", ctx.ApplicationTypeId);
        var category = await ctx.Service.AddCategoryAsync(application.Id, "Scoring");
        var question = await ctx.Service.AddQuestionAsync(category.Id, "Scorable?", AnswerType.Textbox);
        await ctx.Service.AddRuleAsync(question.Id, RuleFamily.Presence, 1);
        await ctx.Service.PublishAsync(application.Id);

        var first = await ctx.Service.OpenDraftAsync(application.Id);
        var second = await ctx.Service.OpenDraftAsync(application.Id);

        first.Version.Id.Should().BePositive();
        first.Version.Id.Should().Be(second.Version.Id);
        first.Version.Status.Should().Be(VersionStatus.Draft);
        first.Version.VersionNumber.Should().Be(2);
        first.Categories.Should().ContainSingle().Which.Name.Should().Be("Scoring");

        using var freshContext = new QuestionAppBuilderDbContext(ctx.Options);
        var storedVersions = await freshContext.ApplicationVersions.ToListAsync();
        storedVersions.Should().HaveCount(2);

        var storedDraft = await new ApplicationRepository(freshContext).GetVersionGraphAsync(first.Version.Id);
        storedDraft.Version.Status.Should().Be(VersionStatus.Draft);
        storedDraft.Categories.Should().ContainSingle().Which.Name.Should().Be("Scoring");
    }

    [Fact]
    public async Task GetApplication_throws_for_unknown_id()
    {
        using var ctx = new TestContext();

        Func<Task> act = () => ctx.Service.GetApplicationAsync(999_999);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    private sealed class TestContext : IDisposable
    {
        public TestContext()
        {
            Options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
                .UseInMemoryDatabase($"application-design-{Guid.NewGuid():N}")
                .Options;

            Context = new QuestionAppBuilderDbContext(Options);
            Repository = new ApplicationRepository(Context);
            Service = new ApplicationDesignService(Repository);

            var applicationType = new ApplicationType { Name = "RFA", SortOrder = 1 };
            Context.ApplicationTypes.Add(applicationType);
            Context.SaveChanges();
            ApplicationTypeId = applicationType.Id;
        }

        public DbContextOptions<QuestionAppBuilderDbContext> Options { get; }
        public QuestionAppBuilderDbContext Context { get; }
        public IApplicationRepository Repository { get; }
        public IApplicationDesignService Service { get; }
        public int ApplicationTypeId { get; }

        public void Dispose() => Context.Dispose();
    }
}
