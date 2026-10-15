using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Repositories;

namespace QuestionAppBuilder.Application.Tests.Services;

public class SubmissionServiceTests
{
    [Fact]
    public async Task Submits_and_persists_scored_snapshot()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Scored", ctx.ApplicationTypeId);

        var standard = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var textQuestion = await ctx.Design.AddQuestionAsync(standard.Id, "Explain", AnswerType.Textbox);
        await ctx.Design.AddRuleAsync(textQuestion.Id, RuleFamily.Presence, 2);
        var numberQuestion = await ctx.Design.AddQuestionAsync(standard.Id, "Years", AnswerType.Number);
        await ctx.Design.AddRuleAsync(numberQuestion.Id, RuleFamily.NumericRange, 3, minValue: 10m);

        var extra = await ctx.Design.AddCategoryAsync(application.Id, "Extra");
        var checkboxQuestion = await ctx.Design.AddQuestionAsync(extra.Id, "Pick all that apply", AnswerType.Checkboxes);
        var firstOption = await ctx.Design.AddOptionAsync(checkboxQuestion.Id, "First", "first");
        var secondOption = await ctx.Design.AddOptionAsync(checkboxQuestion.Id, "Second", "second");
        await ctx.Design.AddRuleAsync(checkboxQuestion.Id, RuleFamily.Selection, 4, MembershipOperator.AnyOf, new[] { firstOption.Id, secondOption.Id });

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        var submission = await ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [textQuestion.Id] = new("Some text", null, null),
                [numberQuestion.Id] = new(null, 15m, null),
                [checkboxQuestion.Id] = new(null, null, new[] { firstOption.Id, secondOption.Id })
            }));

        using var fresh = ctx.CreateFreshContext();
        var stored = await fresh.Submissions
            .Include(s => s.Answers).ThenInclude(a => a.SelectedOptions)
            .Include(s => s.CategoryScores)
            .SingleAsync(s => s.Id == submission.Id);

        stored.ApplicationId.Should().Be(application.Id);
        stored.ApplicationVersionId.Should().Be(versionId);
        stored.TotalScore.Should().Be(9);

        stored.Answers.Should().HaveCount(3);
        var textAnswer = stored.Answers.Single(a => a.QuestionId == textQuestion.Id);
        textAnswer.TextValue.Should().Be("Some text");
        textAnswer.NumberValue.Should().BeNull();
        textAnswer.PointsEarned.Should().Be(2);
        var numberAnswer = stored.Answers.Single(a => a.QuestionId == numberQuestion.Id);
        numberAnswer.TextValue.Should().BeNull();
        numberAnswer.NumberValue.Should().Be(15m);
        numberAnswer.PointsEarned.Should().Be(3);
        var checkboxAnswer = stored.Answers.Single(a => a.QuestionId == checkboxQuestion.Id);
        checkboxAnswer.PointsEarned.Should().Be(4);
        checkboxAnswer.SelectedOptions.Select(o => o.QuestionOptionId).Should().BeEquivalentTo(new[] { firstOption.Id, secondOption.Id });

        stored.CategoryScores.Should().HaveCount(2);
        stored.CategoryScores.Single(c => c.CategoryId == standard.Id).Score.Should().Be(5);
        stored.CategoryScores.Single(c => c.CategoryId == extra.Id).Score.Should().Be(4);
    }

    [Fact]
    public async Task Unknown_question_id_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Unknown ids", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var question = await ctx.Design.AddQuestionAsync(category.Id, "Name", AnswerType.Textbox);
        await ctx.Design.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        Func<Task> act = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [question.Id] = new("ok", null, null),
                [9999] = new("ghost", null, null)
            }));

        var exception = await act.Should().ThrowAsync<AnswerValidationException>();
        exception.Which.QuestionIds.Should().BeEquivalentTo(new[] { 9999 });

        using var fresh = ctx.CreateFreshContext();
        fresh.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Option_from_another_question_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Foreign options", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var firstQuestion = await ctx.Design.AddQuestionAsync(category.Id, "First", AnswerType.Checkboxes);
        await ctx.Design.AddOptionAsync(firstQuestion.Id, "First option", "first");
        var secondQuestion = await ctx.Design.AddQuestionAsync(category.Id, "Second", AnswerType.Checkboxes);
        var secondOption = await ctx.Design.AddOptionAsync(secondQuestion.Id, "Second option", "second");

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        Func<Task> act = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [firstQuestion.Id] = new(null, null, new[] { secondOption.Id }),
                [secondQuestion.Id] = new(null, null, new[] { secondOption.Id })
            }));

        var exception = await act.Should().ThrowAsync<AnswerValidationException>();
        exception.Which.QuestionIds.Should().BeEquivalentTo(new[] { firstQuestion.Id });

        using var fresh = ctx.CreateFreshContext();
        fresh.Submissions.Should().BeEmpty();
        fresh.Answers.Should().BeEmpty();
        fresh.SubmissionCategoryScores.Should().BeEmpty();
    }

    [Fact]
    public async Task Misplaced_values_are_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Misplaced", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var numberQuestion = await ctx.Design.AddQuestionAsync(category.Id, "Age", AnswerType.Number, isRequired: false);
        var textQuestion = await ctx.Design.AddQuestionAsync(category.Id, "Name", AnswerType.Textbox, isRequired: false);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        Func<Task> textOnNumber = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [numberQuestion.Id] = new("not a number", null, null)
            }));

        var textOnNumberException = await textOnNumber.Should().ThrowAsync<AnswerValidationException>();
        textOnNumberException.Which.QuestionIds.Should().BeEquivalentTo(new[] { numberQuestion.Id });

        Func<Task> idsOnTextbox = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [textQuestion.Id] = new(null, null, Array.Empty<int>())
            }));

        var idsOnTextboxException = await idsOnTextbox.Should().ThrowAsync<AnswerValidationException>();
        idsOnTextboxException.Which.QuestionIds.Should().BeEquivalentTo(new[] { textQuestion.Id });

        using var fresh = ctx.CreateFreshContext();
        fresh.Submissions.Should().BeEmpty();
        fresh.Answers.Should().BeEmpty();
        fresh.SubmissionCategoryScores.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_required_in_standard_category_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Required", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var firstQuestion = await ctx.Design.AddQuestionAsync(category.Id, "First", AnswerType.Textbox);
        var secondQuestion = await ctx.Design.AddQuestionAsync(category.Id, "Second", AnswerType.Textbox);
        await ctx.Design.AddRuleAsync(firstQuestion.Id, RuleFamily.Presence, 1);
        await ctx.Design.AddRuleAsync(secondQuestion.Id, RuleFamily.Presence, 1);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        Func<Task> act = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>()));

        var exception = await act.Should().ThrowAsync<AnswerValidationException>();
        exception.Which.QuestionIds.Should().BeEquivalentTo(new[] { firstQuestion.Id, secondQuestion.Id });
    }

    [Fact]
    public async Task Independent_category_may_be_left_empty()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Independent", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Background", isIndependent: true);
        var question = await ctx.Design.AddQuestionAsync(category.Id, "Context", AnswerType.Textbox);
        await ctx.Design.AddRuleAsync(question.Id, RuleFamily.Presence, 5);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        var submission = await ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>()));

        using var fresh = ctx.CreateFreshContext();
        var stored = await fresh.Submissions
            .Include(s => s.CategoryScores)
            .SingleAsync(s => s.Id == submission.Id);

        stored.TotalScore.Should().Be(0);
        stored.CategoryScores.Should().ContainSingle();
        stored.CategoryScores[0].CategoryId.Should().Be(category.Id);
        stored.CategoryScores[0].Score.Should().Be(0);
    }

    [Fact]
    public async Task Zero_answers_persist_with_zero_total()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("All optional", ctx.ApplicationTypeId);
        var first = await ctx.Design.AddCategoryAsync(application.Id, "First");
        var firstQuestion = await ctx.Design.AddQuestionAsync(first.Id, "First question", AnswerType.Textbox, isRequired: false);
        await ctx.Design.AddRuleAsync(firstQuestion.Id, RuleFamily.Presence, 2);
        var second = await ctx.Design.AddCategoryAsync(application.Id, "Second");
        var secondQuestion = await ctx.Design.AddQuestionAsync(second.Id, "Second question", AnswerType.Number, isRequired: false);
        await ctx.Design.AddRuleAsync(secondQuestion.Id, RuleFamily.Presence, 3);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        var submission = await ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>()));

        using var fresh = ctx.CreateFreshContext();
        var stored = await fresh.Submissions
            .Include(s => s.Answers)
            .Include(s => s.CategoryScores)
            .SingleAsync(s => s.Id == submission.Id);

        stored.TotalScore.Should().Be(0);
        stored.Answers.Should().BeEmpty();
        stored.CategoryScores.Should().HaveCount(2);
        stored.CategoryScores.Should().OnlyContain(score => score.Score == 0);
        stored.CategoryScores.Select(score => score.CategoryId).Should().BeEquivalentTo(new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task Over_length_text_is_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Long text", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var question = await ctx.Design.AddQuestionAsync(category.Id, "Essay", AnswerType.Textarea);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        var tooLong = new string('x', ValidationLimits.AnswerTextMaxLength + 1);

        Func<Task> act = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [question.Id] = new(tooLong, null, null)
            }));

        var exception = await act.Should().ThrowAsync<AnswerValidationException>();
        exception.Which.QuestionIds.Should().BeEquivalentTo(new[] { question.Id });

        using var fresh = ctx.CreateFreshContext();
        fresh.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Unpublished_or_mismatched_version_is_not_found()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Publisher", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var question = await ctx.Design.AddQuestionAsync(category.Id, "Name", AnswerType.Textbox);
        await ctx.Design.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        var publishedVersionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        await ctx.Design.AddCategoryAsync(application.Id, "Cloned draft category");
        var draftVersionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;

        Func<Task> draft = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            draftVersionId,
            new Dictionary<int, AnswerValue> { [question.Id] = new("ok", null, null) }));

        await draft.Should().ThrowAsync<NotFoundException>();

        var other = await ctx.Design.CreateApplicationAsync("Other", ctx.ApplicationTypeId);
        Func<Task> mismatched = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            other.Id,
            publishedVersionId,
            new Dictionary<int, AnswerValue> { [question.Id] = new("ok", null, null) }));

        await mismatched.Should().ThrowAsync<NotFoundException>();

        using var fresh = ctx.CreateFreshContext();
        fresh.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Anonymous_submission_has_null_user()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Anonymous", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var question = await ctx.Design.AddQuestionAsync(category.Id, "Name", AnswerType.Textbox);
        await ctx.Design.AddRuleAsync(question.Id, RuleFamily.Presence, 1);

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        var submission = await ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue> { [question.Id] = new("Anonymous", null, null) }));

        using var fresh = ctx.CreateFreshContext();
        var stored = await fresh.Submissions.SingleAsync(s => s.Id == submission.Id);
        stored.UserId.Should().BeNull();
        stored.TotalScore.Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_selected_option_ids_are_rejected()
    {
        using var ctx = new TestContext();
        var application = await ctx.Design.CreateApplicationAsync("Duplicate selections", ctx.ApplicationTypeId);
        var category = await ctx.Design.AddCategoryAsync(application.Id, "Standard");
        var question = await ctx.Design.AddQuestionAsync(category.Id, "Pick all that apply", AnswerType.Checkboxes);
        var option = await ctx.Design.AddOptionAsync(question.Id, "First", "first");

        var versionId = (await ctx.Design.GetDraftGraphAsync(application.Id)).Version.Id;
        await ctx.Design.PublishAsync(application.Id);

        Func<Task> act = () => ctx.Submissions.SubmitAsync(new SubmitRequest(
            application.Id,
            versionId,
            new Dictionary<int, AnswerValue>
            {
                [question.Id] = new(null, null, new[] { option.Id, option.Id })
            }));

        var exception = await act.Should().ThrowAsync<AnswerValidationException>();
        exception.Which.QuestionIds.Should().BeEquivalentTo(new[] { question.Id });

        using var fresh = ctx.CreateFreshContext();
        fresh.Submissions.Should().BeEmpty();
        fresh.Answers.Should().BeEmpty();
    }

    private sealed class TestContext : IDisposable
    {
        public TestContext()
        {
            Options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
                .UseInMemoryDatabase($"submission-{Guid.NewGuid():N}")
                .Options;

            Context = new QuestionAppBuilderDbContext(Options);
            var applicationRepository = new ApplicationRepository(Context);
            Design = new ApplicationDesignService(applicationRepository);
            Submissions = new SubmissionService(applicationRepository, new SubmissionRepository(Context), new ScoringEngine());

            var applicationType = new ApplicationType { Name = "RFA", SortOrder = 1 };
            Context.ApplicationTypes.Add(applicationType);
            Context.SaveChanges();
            ApplicationTypeId = applicationType.Id;
        }

        public DbContextOptions<QuestionAppBuilderDbContext> Options { get; }
        public QuestionAppBuilderDbContext Context { get; }
        public IApplicationDesignService Design { get; }
        public ISubmissionService Submissions { get; }
        public int ApplicationTypeId { get; }

        public QuestionAppBuilderDbContext CreateFreshContext() => new(Options);

        public void Dispose() => Context.Dispose();
    }
}
