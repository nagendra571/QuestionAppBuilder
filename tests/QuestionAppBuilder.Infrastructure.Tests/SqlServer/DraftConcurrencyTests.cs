using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Authoring;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class DraftConcurrencyTests
{
    [SqlServerFact]
    public async Task Publish_racing_save_never_publishes_an_unreviewed_revision()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var drafts = new DraftService(new DatabaseContextFactory(database), new DefinitionEvaluator());
        await SeedDraftAsync(database);
        var reviewed = await drafts.GetAsync(1);
        var edit = await drafts.GetAsync(1);
        edit.Definition.Version.DisplayName = "Edited after review";
        await drafts.SaveAsync(new SaveDraftRequest(1, edit.Revision, edit.Definition));

        Func<Task> stalePublish = () => drafts.PublishAsync(new PublishRequest(1, reviewed.Revision, new HashSet<string>()));
        await stalePublish.Should().ThrowAsync<DraftConflictException>();
        await using var context = database.CreateContext();
        (await context.Applications.SingleAsync()).CurrentVersionId.Should().BeNull();
        (await context.ApplicationVersions.SingleAsync()).Status.Should().Be(VersionStatus.Draft);
    }

    [SqlServerFact]
    public async Task Warning_acknowledgment_is_invalid_after_edit()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await SeedDraftAsync(database, includeOverlappingRules: true);
        var drafts = new DraftService(new DatabaseContextFactory(database), new DefinitionEvaluator());
        var reviewed = await drafts.GetAsync(1);
        var review = await drafts.ReviewAsync(1);
        review.Warnings.Select(x => x.Code).Should().Contain("overlapping-numeric-rules");
        var edited = await drafts.GetAsync(1);
        edited.Definition.Categories.Single().Elements.Single().Question!.Rules[1].MinValue = 11;
        await drafts.SaveAsync(new SaveDraftRequest(1, edited.Revision, edited.Definition));

        Func<Task> stalePublish = () => drafts.PublishAsync(new PublishRequest(1, reviewed.Revision, review.Warnings.Select(x => x.Code).ToHashSet()));
        await stalePublish.Should().ThrowAsync<DraftConflictException>();
    }

    [SqlServerFact]
    public async Task Published_graph_cannot_be_overwritten()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await SeedDraftAsync(database);
        var drafts = new DraftService(new DatabaseContextFactory(database), new DefinitionEvaluator());
        var snapshot = await drafts.GetAsync(1);
        await drafts.PublishAsync(new PublishRequest(1, snapshot.Revision, new HashSet<string>()));
        snapshot.Definition.Version.DisplayName = "Tampered";

        Func<Task> staleSave = () => drafts.SaveAsync(new SaveDraftRequest(1, snapshot.Revision, snapshot.Definition));
        await staleSave.Should().ThrowAsync<NotFoundException>();
        await using var context = database.CreateContext();
        (await context.ApplicationVersions.SingleAsync()).DisplayName.Should().Be("Draft revision");
    }

    [SqlServerFact]
    public async Task Concurrent_child_edits_reject_stale_revision_and_preserve_winner()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await SeedDraftAsync(database);
        var drafts = new DraftService(new DatabaseContextFactory(database), new DefinitionEvaluator());
        var firstView = await drafts.GetAsync(1);
        var staleView = await drafts.GetAsync(1);
        firstView.Definition.Categories[0].Name = "Winner";
        staleView.Definition.Categories[0].Name = "Stale overwrite";
        var saved = await drafts.SaveAsync(new SaveDraftRequest(1, firstView.Revision, firstView.Definition));

        Func<Task> staleSave = () => drafts.SaveAsync(new SaveDraftRequest(1, staleView.Revision, staleView.Definition));
        await staleSave.Should().ThrowAsync<DraftConflictException>();

        var persisted = await drafts.GetAsync(1);
        persisted.Definition.Categories.Single().Name.Should().Be("Winner");
        persisted.Revision.Should().NotEqual(firstView.Revision);
        saved.Definition.Categories.Single().Name.Should().Be("Winner");
    }

    private static async Task SeedDraftAsync(SqlServerDatabase database, bool includeOverlappingRules = false)
    {
        await using var seed = database.CreateContext();
        await seed.Database.MigrateAsync();
        var question = new Question { AnswerType = includeOverlappingRules ? AnswerType.Number : AnswerType.Textbox, IsRequired = true };
        if (includeOverlappingRules)
        {
            question.ScoringMode = ScoringMode.FirstMatch;
            question.Rules.AddRange(
            [
                new ScoringRule { Family = RuleFamily.NumericRange, Points = 1, MinValue = 0, MaxValue = 10, SortOrder = 1 },
                new ScoringRule { Family = RuleFamily.NumericRange, Points = 2, MinValue = 5, MaxValue = 15, SortOrder = 2 }
            ]);
        }
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Draft revision", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow, DisplayName = "Draft revision" } }
        };
        app.Versions.Single().Categories.Add(new Category
        {
            Name = "Original", SortOrder = 1,
            Elements = { new Element { Kind = ElementKind.Question, Label = "Name", SortOrder = 1, Question = question } }
        });
        seed.Applications.Add(app);
        await seed.SaveChangesAsync();
    }

    private sealed class DatabaseContextFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext());
    }
}
