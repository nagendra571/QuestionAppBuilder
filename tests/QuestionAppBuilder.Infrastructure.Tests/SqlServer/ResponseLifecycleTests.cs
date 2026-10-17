using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class ResponseLifecycleTests
{
    [SqlServerFact]
    public async Task Save_then_new_context_resume_preserves_answers()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var questionId = db.QuestionId;
        var saved = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [questionId] = new AnswerValue("saved text", null, null) }));
        var (resumedService, _) = db.CreateService("owner-1");

        var resumed = await resumedService.GetAsync(started.Id);

        resumed.Id.Should().Be(started.Id);
        resumed.ApplicationVersionId.Should().Be(started.ApplicationVersionId);
        resumed.Answers[questionId].Text.Should().Be("saved text");
        resumed.Revision.Should().Equal(saved.Revision);
    }

    [SqlServerFact]
    public async Task Another_owner_gets_not_found_for_read_and_write()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var (otherOwner, _) = db.CreateService("owner-2");

        Func<Task> read = () => otherOwner.GetAsync(started.Id);
        Func<Task> write = () => otherOwner.SaveAsync(new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue>()));

        await read.Should().ThrowAsync<NotFoundException>();
        await write.Should().ThrowAsync<NotFoundException>();
    }

    [SqlServerFact]
    public async Task Two_saves_same_revision_one_conflicts()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var questionId = db.QuestionId;
        var request1 = new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue> { [questionId] = new("one", null, null) });
        var request2 = new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue> { [questionId] = new("two", null, null) });
        await service.SaveAsync(request1);

        Func<Task> stale = () => service.SaveAsync(request2);

        await stale.Should().ThrowAsync<ResponseConflictException>();
        (await service.GetAsync(started.Id)).Answers[questionId].Text.Should().Be("one");
    }

    [SqlServerFact]
    public async Task Publish_new_version_preserves_existing_response_version()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        await using (var context = db.CreateContext())
        {
            var v2 = new ApplicationVersion { ApplicationId = 1, VersionNumber = 2, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow, PublishedUtc = DateTime.UtcNow, DisplayName = "v2" };
            context.ApplicationVersions.Add(v2);
            await context.SaveChangesAsync();
            var app = await context.Applications.SingleAsync();
            app.CurrentVersionId = v2.Id;
            await context.SaveChangesAsync();
        }

        var resumed = await service.GetAsync(started.Id);

        resumed.ApplicationVersionId.Should().Be(started.ApplicationVersionId);
        resumed.Definition.Version.VersionNumber.Should().Be(1);
    }

    [SqlServerFact]
    public async Task Closed_submissions_still_allow_owned_draft_save()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        await using (var context = db.CreateContext())
        {
            var app = await context.Applications.SingleAsync();
            app.AcceptSubmissions = false;
            await context.SaveChangesAsync();
        }

        var saved = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("still saving", null, null) }));

        saved.Answers[db.QuestionId].Text.Should().Be("still saving");
    }

    [SqlServerFact]
    public async Task Invalid_option_is_rejected_but_missing_required_saves()
    {
        await using var db = await TestDatabase.CreateAsync(choiceQuestion: true);
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var (optionId, _) = db.ChoiceIds;

        var incomplete = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue>()));
        incomplete.Answers.Should().BeEmpty();
        Func<Task> invalid = () => service.SaveAsync(new SaveResponseRequest(started.Id, incomplete.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, null, [int.MaxValue]) }));

        await invalid.Should().ThrowAsync<AnswerValidationException>();
        var good = await service.SaveAsync(new SaveResponseRequest(started.Id, incomplete.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, null, [optionId]) }));
        good.Answers[db.QuestionId].SelectedOptionIds.Should().ContainSingle().Which.Should().Be(optionId);
    }

    [SqlServerFact]
    public async Task Empty_field_replaces_prior_value()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var questionId = db.QuestionId;
        var first = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [questionId] = new("old value", null, null) }));

        var cleared = await service.SaveAsync(new SaveResponseRequest(started.Id, first.Revision,
            new Dictionary<int, AnswerValue> { [questionId] = new(null, null, null) }));

        cleared.Answers.Should().NotContainKey(questionId);
    }

    [SqlServerFact]
    public async Task Numeric_input_requiring_decimal_truncation_is_rejected()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.Number);
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        Func<Task> lossy = () => service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, 1.23456m, null) }));

        await lossy.Should().ThrowAsync<AnswerValidationException>();
        var exact = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, 1.2345m, null) }));
        exact.Answers[db.QuestionId].Number.Should().Be(1.2345m);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqlServerDatabase _sql;
        private readonly DefinitionEvaluator _evaluator = new();
        private TestDatabase(SqlServerDatabase sql) => _sql = sql;
        public int QuestionId { get; private set; }
        public (int First, int Second) ChoiceIds { get; private set; }
        public QuestionAppBuilderDbContext CreateContext() => _sql.CreateContext();
        public (IResponseService Service, TestRespondentContext Identity) CreateService(string owner)
        {
            var identity = new TestRespondentContext(owner);
            return (new ResponseService(new DatabaseContextFactory(_sql), identity), identity);
        }
        public static async Task<TestDatabase> CreateAsync(bool choiceQuestion = false, AnswerType answerType = AnswerType.Textbox)
        {
            var db = new TestDatabase(await SqlServerDatabase.CreateAsync());
            await using var context = db.CreateContext();
            await context.Database.MigrateAsync();
            var question = new Question { AnswerType = choiceQuestion ? AnswerType.Dropdown : answerType, IsRequired = true };
            if (choiceQuestion)
            {
                question.Options.Add(new QuestionOption { Label = "One", Value = "one", SortOrder = 1 });
                question.Options.Add(new QuestionOption { Label = "Two", Value = "two", SortOrder = 2 });
            }
            var app = new QuestionAppBuilder.Domain.Entities.Application
            {
                Name = "Response lifecycle", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                AcceptNewResponses = true, AcceptSubmissions = true,
                Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow,
                    PublishedUtc = DateTime.UtcNow, DisplayName = "v1", Categories = { new Category { Name = "Basics", SortOrder = 1,
                        Elements = { new Element { Kind = ElementKind.Question, Label = "Answer", SortOrder = 1, Question = question } } } } } }
            };
            context.Applications.Add(app);
            await context.SaveChangesAsync();
            app.CurrentVersionId = app.Versions.Single().Id;
            await context.SaveChangesAsync();
            db.QuestionId = question.Id;
            if (choiceQuestion) db.ChoiceIds = (question.Options[0].Id, question.Options[1].Id);
            return db;
        }
        public ValueTask DisposeAsync() => _sql.DisposeAsync();
    }

    private sealed class TestRespondentContext(string owner) : IRespondentContext
    {
        public RespondentIdentity GetRequiredIdentity() => new(owner, null, null);
    }

    private sealed class DatabaseContextFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(database.CreateContext());
    }
}
