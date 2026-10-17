using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class SubmissionConcurrencyTests
{
    [SqlServerFact]
    public async Task Concurrent_submit_same_response_creates_one_record()
    {
        await using var db = await Fixture.CreateAsync();
        var started = await db.StartAndSaveAsync();
        var requestId = Guid.NewGuid();
        var request = new SubmitResponseRequest(started.Id, started.Revision, requestId);
        var receipts = await Task.WhenAll(db.Coordinator().SubmitAsync(request), db.Coordinator().SubmitAsync(request));
        receipts.Select(x => x.SubmissionId).Distinct().Should().ContainSingle();
        await using var context = db.Context();
        (await context.Submissions.CountAsync(s => s.ResponseId == started.Id)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Retry_after_closure_returns_original_receipt()
    {
        await using var db = await Fixture.CreateAsync();
        var started = await db.StartAndSaveAsync();
        var request = new SubmitResponseRequest(started.Id, started.Revision, Guid.NewGuid());
        var first = await db.Coordinator().SubmitAsync(request);
        await db.CloseAsync();
        (await db.Coordinator().SubmitAsync(request)).Should().BeEquivalentTo(first);
    }

    [SqlServerFact]
    public async Task Stale_save_revision_cannot_submit_newer_answers()
    {
        await using var db = await Fixture.CreateAsync();
        var started = await db.StartAndSaveAsync();
        var newer = await db.SaveAsync(started.Id, started.Revision, "newer answer");
        Func<Task> submit = () => db.Coordinator().SubmitAsync(new SubmitResponseRequest(started.Id, started.Revision, Guid.NewGuid()));
        await submit.Should().ThrowAsync<ResponseConflictException>();
        (await db.ResponseService().GetAsync(started.Id)).Answers[db.QuestionId].Text.Should().Be("newer answer");
    }

    [SqlServerFact]
    public async Task Closure_commits_first_blocks_submit_without_losing_draft()
    {
        await using var db = await Fixture.CreateAsync();
        var started = await db.StartAndSaveAsync();
        await db.CloseAsync();
        Func<Task> submit = () => db.Coordinator().SubmitAsync(new SubmitResponseRequest(started.Id, started.Revision, Guid.NewGuid()));
        await submit.Should().ThrowAsync<InvalidOperationException>();
        var saved = await db.SaveAsync(started.Id, started.Revision, "still a draft");
        saved.Answers[db.QuestionId].Text.Should().Be("still a draft");
    }

    [SqlServerFact]
    public async Task Submission_commits_first_remains_valid()
    {
        await using var db = await Fixture.CreateAsync();
        var started = await db.StartAndSaveAsync();
        var request = new SubmitResponseRequest(started.Id, started.Revision, Guid.NewGuid());
        var receipt = await db.Coordinator().SubmitAsync(request);
        await db.CloseAsync();
        (await db.Coordinator().SubmitAsync(request)).SubmissionId.Should().Be(receipt.SubmissionId);
    }

    [SqlServerFact]
    public async Task Confirmation_only_receipt_contains_no_scores()
    {
        await using var db = await Fixture.CreateAsync(ResultVisibility.ConfirmationOnly);
        var started = await db.StartAndSaveAsync();
        var receipt = await db.Coordinator().SubmitAsync(new SubmitResponseRequest(started.Id, started.Revision, Guid.NewGuid()));
        receipt.Total.Should().BeNull();
        receipt.Categories.Should().BeNull();
    }

    [SqlServerFact]
    public async Task Hidden_values_are_removed_from_draft_and_final_answers_atomically()
    {
        await using var db = await Fixture.CreateAsync(includeHiddenQuestion: true);
        var started = await db.ResponseService().StartAsync(1);
        var saved = await db.ResponseService().SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, QuestionAppBuilder.Application.Scoring.AnswerValue> { [db.HiddenQuestionId] = new("hidden value", null, null) }));
        var receipt = await db.Coordinator().SubmitAsync(new SubmitResponseRequest(started.Id, saved.Revision, Guid.NewGuid()));
        await using var context = db.Context();
        (await context.ResponseAnswers.AnyAsync(a => a.ResponseDraftId == started.Id)).Should().BeFalse();
        (await context.Answers.AnyAsync(a => a.SubmissionId == receipt.SubmissionId && a.QuestionId == db.HiddenQuestionId)).Should().BeFalse();
        (await context.SubmissionQuestionStates.SingleAsync(s => s.SubmissionId == receipt.SubmissionId && s.QuestionId == db.HiddenQuestionId)).IsApplicable.Should().BeFalse();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqlServerDatabase _sql;
        private Fixture(SqlServerDatabase sql) => _sql = sql;
        public int QuestionId { get; private set; }
        public int HiddenQuestionId { get; private set; }
        public QuestionAppBuilderDbContext Context() => _sql.CreateContext();
        private TestIdentity Identity() => new("test-owner");
        public IResponseService ResponseService() => new ResponseService(new Factory(_sql), Identity());
        public ISubmissionCoordinator Coordinator() => new SubmissionCoordinator(new Factory(_sql), Identity(), new DefinitionEvaluator());
        public static async Task<Fixture> CreateAsync(ResultVisibility visibility = ResultVisibility.Total, bool includeHiddenQuestion = false)
        {
            var db = new Fixture(await SqlServerDatabase.CreateAsync());
            await using var context = db.Context();
            await context.Database.MigrateAsync();
            var question = new Question { AnswerType = AnswerType.Textbox, IsRequired = false };
            var hidden = new Question { AnswerType = AnswerType.Textbox, IsRequired = true };
            var version = new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow,
                PublishedUtc = DateTime.UtcNow, DisplayName = "v1", ResultVisibility = visibility,
                Categories = { new Category { Name = "Basics", SortOrder = 1, Elements = { new Element { Kind = ElementKind.Question, Label = "Answer", SortOrder = 1, Question = question } } } } };
            if (includeHiddenQuestion)
            {
                var second = new Category { Name = "Conditional", SortOrder = 2 };
                second.Elements.Add(new Element { Kind = ElementKind.Question, Label = "Conditional answer", SortOrder = 1, Question = hidden });
                version.Categories.Add(second);
                version.VisibilityConditions.Add(new VisibilityCondition { Junction = ConditionJunction.All,
                    Predicates = { new VisibilityPredicate { SourceQuestion = question, Operator = ConditionOperator.Answered } } });
                second.Elements[0].VisibilityCondition = version.VisibilityConditions[0];
            }
            var app = new QuestionAppBuilder.Domain.Entities.Application { Name = "Submission coordination", ApplicationTypeId = 1,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, AcceptNewResponses = true, AcceptSubmissions = true,
                Versions = { version } };
            context.Applications.Add(app); await context.SaveChangesAsync(); app.CurrentVersionId = app.Versions.Single().Id; await context.SaveChangesAsync();
            db.QuestionId = question.Id;
            db.HiddenQuestionId = hidden.Id;
            return db;
        }
        public async Task<ResponseSnapshot> StartAndSaveAsync()
        {
            var started = await ResponseService().StartAsync(1);
            return await SaveAsync(started.Id, started.Revision, "complete answer");
        }
        public Task<ResponseSnapshot> SaveAsync(Guid responseId, byte[] revision, string text) => ResponseService().SaveAsync(
            new SaveResponseRequest(responseId, revision, new Dictionary<int, AnswerValue> { [QuestionId] = new(text, null, null) }));
        public async Task CloseAsync()
        {
            await using var context = Context(); var app = await context.Applications.SingleAsync();
            var revision = app.RowVersion.ToArray();
            await new AvailabilityService(new Factory(_sql), new TestActor("admin-1")).SetAsync(new AvailabilityUpdate(app.Id, revision, true, false));
            var closed = await context.Applications.AsNoTracking().SingleAsync();
            closed.AcceptNewResponses.Should().BeFalse();
            closed.AcceptSubmissions.Should().BeFalse();
            (await context.ApplicationAuditEvents.SingleAsync()).ActorKey.Should().Be("admin-1");
        }
        public ValueTask DisposeAsync() => _sql.DisposeAsync();
    }

    private sealed class TestIdentity(string owner) : IRespondentContext
    { public RespondentIdentity GetRequiredIdentity() => new(owner, null, null); }
    private sealed class TestActor(string actorKey) : ITrustedActorContext
    { public string GetRequiredActorKey() => actorKey; }
    private sealed class Factory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(database.CreateContext());
    }
}
