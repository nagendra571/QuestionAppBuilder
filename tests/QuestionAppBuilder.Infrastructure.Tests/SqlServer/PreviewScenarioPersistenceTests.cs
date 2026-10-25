using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Repositories;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class PreviewScenarioPersistenceTests
{
    [SqlServerFact]
    public async Task Scenarios_persist_across_contexts_and_updates_require_the_current_rowversion_without_touching_draft()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var repository = new PreviewScenarioRepository(new DatabaseFactory(database));
        var scenario = NewScenario(seeded.ApplicationId, "Capacity boundary", "{\"schemaVersion\":1}");
        int publishedVersionId;
        int submissionId;
        await using (var before = database.CreateContext())
        {
            var published = new ApplicationVersion
            {
                ApplicationId = seeded.ApplicationId, VersionNumber = 2, Status = VersionStatus.Published,
                CreatedUtc = DateTime.UtcNow, PublishedUtc = DateTime.UtcNow, DisplayName = "Frozen definition"
            };
            before.ApplicationVersions.Add(published);
            await before.SaveChangesAsync();
            publishedVersionId = published.Id;
            var submission = new Submission
            {
                ApplicationId = seeded.ApplicationId, ApplicationVersionId = published.Id,
                SubmittedUtc = DateTime.UtcNow, TotalScore = 18, EngineVersion = "frozen-v1"
            };
            before.Submissions.Add(submission);
            await before.SaveChangesAsync();
            submissionId = submission.Id;
        }

        var created = await repository.SaveAsync(scenario, seeded.VersionId, seeded.DraftRevision, null);
        created.RowVersion.Should().NotBeEmpty();
        await using (var independent = database.CreateContext())
        {
            var loaded = await independent.PreviewScenarios.SingleAsync(x => x.Id == scenario.Id);
            loaded.Name.Should().Be(scenario.Name);
            loaded.PayloadJson.Should().Be(scenario.PayloadJson);
        }

        var update = NewScenario(seeded.ApplicationId, "Capacity exceeded", "{\"schemaVersion\":1,\"answers\":[]}");
        update.Id = scenario.Id;
        update.CreatedUtc = created.CreatedUtc;
        var changed = await repository.SaveAsync(update, seeded.VersionId, seeded.DraftRevision, created.RowVersion);
        changed.RowVersion.Should().NotEqual(created.RowVersion);
        Func<Task> stale = () => repository.SaveAsync(scenario, seeded.VersionId, seeded.DraftRevision, created.RowVersion);
        await stale.Should().ThrowAsync<PreviewScenarioConflictException>();

        await using var verify = database.CreateContext();
        (await verify.PreviewScenarios.SingleAsync(x => x.Id == scenario.Id)).Name.Should().Be("Capacity exceeded");
        var draft = await verify.ApplicationVersions.SingleAsync(x => x.Id == seeded.VersionId);
        draft.RevisionNonce.ToByteArray().Should().Equal(seeded.DraftRevision);
        var frozen = await verify.ApplicationVersions.SingleAsync(x => x.Id == publishedVersionId);
        frozen.DisplayName.Should().Be("Frozen definition");
        frozen.Status.Should().Be(VersionStatus.Published);
        var submitted = await verify.Submissions.SingleAsync(x => x.Id == submissionId);
        submitted.TotalScore.Should().Be(18);
        submitted.EngineVersion.Should().Be("frozen-v1");
    }

    [SqlServerFact]
    public async Task Two_creators_at_the_last_slot_are_serialized_and_one_is_rejected()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var repository = new PreviewScenarioRepository(new DatabaseFactory(database));
        for (var i = 0; i < 49; i++)
            await repository.SaveAsync(NewScenario(seeded.ApplicationId, $"Scenario {i}", "{}"), seeded.VersionId, seeded.DraftRevision, null);

        var first = NewScenario(seeded.ApplicationId, "Last slot A", "{}");
        var second = NewScenario(seeded.ApplicationId, "Last slot B", "{}");
        var outcomes = await Task.WhenAll(TryCreateAsync(repository, first, seeded), TryCreateAsync(repository, second, seeded));
        outcomes.Count(x => x).Should().Be(1);
        await using var verify = database.CreateContext();
        (await verify.PreviewScenarios.CountAsync(x => x.ApplicationId == seeded.ApplicationId)).Should().Be(50);
    }

    [SqlServerFact]
    public async Task Concurrent_updates_are_single_winner_and_get_delete_are_application_scoped()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        int otherApplicationId;
        await using (var db = database.CreateContext())
        {
            var other = new ApplicationEntity { Name = "Other Scenario Owner", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            db.Applications.Add(other);
            await db.SaveChangesAsync();
            otherApplicationId = other.Id;
        }
        var repository = new PreviewScenarioRepository(new DatabaseFactory(database));
        var initial = await repository.SaveAsync(NewScenario(seeded.ApplicationId, "Shared only by owner", "{}"),
            seeded.VersionId, seeded.DraftRevision, null);
        (await repository.GetAsync(otherApplicationId, initial.Id)).Should().BeNull();
        Func<Task> wrongOwnerDelete = () => repository.DeleteAsync(otherApplicationId, initial.Id, initial.RowVersion);
        await wrongOwnerDelete.Should().ThrowAsync<PreviewScenarioConflictException>();

        var a = NewScenario(seeded.ApplicationId, "Concurrent A", "{\"value\":1}"); a.Id = initial.Id;
        var b = NewScenario(seeded.ApplicationId, "Concurrent B", "{\"value\":2}"); b.Id = initial.Id;
        var updates = await Task.WhenAll(TryUpdateAsync(repository, a, seeded, initial.RowVersion),
            TryUpdateAsync(repository, b, seeded, initial.RowVersion));
        updates.Count(x => x).Should().Be(1);
        var current = (await repository.GetAsync(seeded.ApplicationId, initial.Id))!;
        (current.Name == "Concurrent A" || current.Name == "Concurrent B").Should().BeTrue();
        Func<Task> staleDelete = () => repository.DeleteAsync(seeded.ApplicationId, initial.Id, initial.RowVersion);
        await staleDelete.Should().ThrowAsync<PreviewScenarioConflictException>();
        await repository.DeleteAsync(seeded.ApplicationId, initial.Id, current.RowVersion);
        (await repository.GetAsync(seeded.ApplicationId, initial.Id)).Should().BeNull();
    }

    [SqlServerFact]
    public async Task Stale_or_wrong_draft_claims_are_rejected_and_published_draft_is_never_recreated()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var repository = new PreviewScenarioRepository(new DatabaseFactory(database, enableRetryOnFailure: true));
        var first = NewScenario(seeded.ApplicationId, "Guarded scenario", "{}");
        var staleRevision = Guid.NewGuid().ToByteArray();
        Func<Task> staleCreate = () => repository.SaveAsync(first, seeded.VersionId, staleRevision, null);
        await staleCreate.Should().ThrowAsync<DraftConflictException>();
        Func<Task> wrongVersionCreate = () => repository.SaveAsync(first, seeded.VersionId + 100, seeded.DraftRevision, null);
        await wrongVersionCreate.Should().ThrowAsync<DraftConflictException>();
        (await repository.GetAsync(seeded.ApplicationId, first.Id)).Should().BeNull();

        var saved = await repository.SaveAsync(first, seeded.VersionId, seeded.DraftRevision, null);
        var update = NewScenario(seeded.ApplicationId, "Must not update", "{\"changed\":true}");
        update.Id = saved.Id;
        Func<Task> staleUpdate = () => repository.SaveAsync(update, seeded.VersionId, staleRevision, saved.RowVersion);
        await staleUpdate.Should().ThrowAsync<DraftConflictException>();
        Func<Task> wrongVersionUpdate = () => repository.SaveAsync(update, seeded.VersionId + 100, seeded.DraftRevision, saved.RowVersion);
        await wrongVersionUpdate.Should().ThrowAsync<DraftConflictException>();
        (await repository.GetAsync(seeded.ApplicationId, saved.Id))!.PayloadJson.Should().Be("{}");

        await using (var db = database.CreateContext())
        {
            var draft = await db.ApplicationVersions.SingleAsync(x => x.Id == seeded.VersionId);
            draft.Status = VersionStatus.Published;
            draft.PublishedUtc = DateTime.UtcNow;
            var application = await db.Applications.SingleAsync(x => x.Id == seeded.ApplicationId);
            application.CurrentVersionId = draft.Id;
            await db.SaveChangesAsync();
        }

        var afterPublication = NewScenario(seeded.ApplicationId, "Must not recreate a draft", "{}");
        Func<Task> createAfterPublication = () => repository.SaveAsync(afterPublication, seeded.VersionId, seeded.DraftRevision, null);
        await createAfterPublication.Should().ThrowAsync<NotFoundException>();
        Func<Task> updateAfterPublication = () => repository.SaveAsync(update, seeded.VersionId, seeded.DraftRevision, saved.RowVersion);
        await updateAfterPublication.Should().ThrowAsync<NotFoundException>();
        await using var verify = database.CreateContext();
        (await verify.ApplicationVersions.CountAsync(x => x.ApplicationId == seeded.ApplicationId)).Should().Be(1);
        (await verify.ApplicationVersions.AnyAsync(x => x.ApplicationId == seeded.ApplicationId && x.Status == VersionStatus.Draft)).Should().BeFalse();
        (await verify.PreviewScenarios.CountAsync(x => x.ApplicationId == seeded.ApplicationId)).Should().Be(1);
        (await verify.PreviewScenarios.SingleAsync(x => x.Id == saved.Id)).PayloadJson.Should().Be("{}");
    }

    [SqlServerFact]
    public async Task Deleting_application_cascades_shared_scenarios()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seeded = await SeedAsync(database);
        var repository = new PreviewScenarioRepository(new DatabaseFactory(database));
        var saved = await repository.SaveAsync(NewScenario(seeded.ApplicationId, "Keep with app", "{}"), seeded.VersionId, seeded.DraftRevision, null);
        await using (var db = database.CreateContext())
        {
            var application = await db.Applications.SingleAsync(x => x.Id == seeded.ApplicationId);
            db.Applications.Remove(application);
            await db.SaveChangesAsync();
        }
        (await repository.GetAsync(seeded.ApplicationId, saved.Id)).Should().BeNull();
    }

    private static async Task<bool> TryCreateAsync(IPreviewScenarioRepository repository, PreviewScenario scenario, Seed seed)
    {
        try
        {
            await repository.SaveAsync(scenario, seed.VersionId, seed.DraftRevision, null);
            return true;
        }
        catch (QuestionAppBuilder.Domain.Exceptions.DefinitionValidationException) { return false; }
    }

    private static async Task<bool> TryUpdateAsync(IPreviewScenarioRepository repository, PreviewScenario scenario, Seed seed, byte[] revision)
    {
        try
        {
            await repository.SaveAsync(scenario, seed.VersionId, seed.DraftRevision, revision);
            return true;
        }
        catch (PreviewScenarioConflictException) { return false; }
    }

    private static async Task<Seed> SeedAsync(SqlServerDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var now = DateTime.UtcNow;
        var application = new ApplicationEntity { Name = "Scenario Storage", ApplicationTypeId = 1, CreatedUtc = now, UpdatedUtc = now };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        var draft = new ApplicationVersion
        {
            ApplicationId = application.Id, VersionNumber = 1, Status = VersionStatus.Draft,
            CreatedUtc = now, DisplayName = "Draft"
        };
        db.ApplicationVersions.Add(draft);
        await db.SaveChangesAsync();
        return new Seed(application.Id, draft.Id, draft.RevisionNonce.ToByteArray());
    }

    private static PreviewScenario NewScenario(int appId, string name, string payload) => new()
    {
        ApplicationId = appId, Name = name, PayloadJson = payload, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
    };

    private sealed record Seed(int ApplicationId, int VersionId, byte[] DraftRevision);

    private sealed class DatabaseFactory(SqlServerDatabase database, bool enableRetryOnFailure = false) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext(enableRetryOnFailure: enableRetryOnFailure);
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext(enableRetryOnFailure: enableRetryOnFailure));
    }
}
