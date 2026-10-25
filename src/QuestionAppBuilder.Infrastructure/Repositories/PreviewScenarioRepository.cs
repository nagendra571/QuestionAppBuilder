using System.Data;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Repositories;

public sealed class PreviewScenarioRepository(IDbContextFactory<QuestionAppBuilderDbContext> factory)
    : IPreviewScenarioRepository
{
    public async Task<IReadOnlyList<PreviewScenario>> ListAsync(int applicationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PreviewScenarios.AsNoTracking().Where(x => x.ApplicationId == applicationId)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<PreviewScenario?> GetAsync(int applicationId, Guid scenarioId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PreviewScenarios.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ApplicationId == applicationId && x.Id == scenarioId, ct);
    }

    public async Task<PreviewScenario> SaveAsync(PreviewScenario scenario, int expectedVersionId,
        byte[] expectedDraftRevision, byte[]? expectedScenarioRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(expectedDraftRevision);
        await using var strategyContext = await factory.CreateDbContextAsync(ct);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await LockApplicationAsync(db, scenario.ApplicationId, ct);
            if (expectedScenarioRevision is null)
            {
                var replay = await db.PreviewScenarios.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ApplicationId == scenario.ApplicationId && x.Id == scenario.Id, ct);
                if (replay is not null)
                {
                    if (replay.Name == scenario.Name && replay.PayloadJson == scenario.PayloadJson)
                        return replay;
                    throw new PreviewScenarioConflictException();
                }
            }
            var draft = await LockCurrentDraftAsync(db, scenario.ApplicationId, ct)
                ?? throw new NotFoundException("No current draft version exists for this application.");
            if (draft.Id != expectedVersionId || !draft.RevisionNonce.ToByteArray().AsSpan().SequenceEqual(expectedDraftRevision))
                throw new DraftConflictException();

            PreviewScenario target;
            if (expectedScenarioRevision is null)
            {
                if (await db.PreviewScenarios.CountAsync(x => x.ApplicationId == scenario.ApplicationId, ct) >= PreviewScenarioLimits.MaximumScenarios)
                    throw new DefinitionValidationException($"An application can have at most {PreviewScenarioLimits.MaximumScenarios} preview scenarios.");
                target = new PreviewScenario { Id = scenario.Id, ApplicationId = scenario.ApplicationId, CreatedUtc = DateTime.UtcNow };
                db.PreviewScenarios.Add(target);
            }
            else
            {
                target = await db.PreviewScenarios.SingleOrDefaultAsync(x => x.ApplicationId == scenario.ApplicationId && x.Id == scenario.Id, ct)
                    ?? throw new PreviewScenarioConflictException();
                if (!target.RowVersion.AsSpan().SequenceEqual(expectedScenarioRevision)) throw new PreviewScenarioConflictException();
                db.Entry(target).Property(x => x.RowVersion).OriginalValue = expectedScenarioRevision;
            }
            target.Name = scenario.Name;
            target.PayloadJson = scenario.PayloadJson;
            target.UpdatedUtc = DateTime.UtcNow;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new PreviewScenarioConflictException(); }
            await tx.CommitAsync(ct);
            return target;
        });
    }

    public async Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedScenarioRevision);
        await using var strategyContext = await factory.CreateDbContextAsync(ct);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await LockApplicationAsync(db, applicationId, ct);
            var target = await db.PreviewScenarios.SingleOrDefaultAsync(x => x.ApplicationId == applicationId && x.Id == scenarioId, ct);
            if (target is null) throw new PreviewScenarioConflictException();
            if (!target.RowVersion.AsSpan().SequenceEqual(expectedScenarioRevision)) throw new PreviewScenarioConflictException();
            db.Entry(target).Property(x => x.RowVersion).OriginalValue = expectedScenarioRevision;
            db.PreviewScenarios.Remove(target);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new PreviewScenarioConflictException(); }
            await tx.CommitAsync(ct);
        });
    }

    private static async Task<ApplicationVersion?> LockCurrentDraftAsync(QuestionAppBuilderDbContext db, int applicationId, CancellationToken ct)
    {
        // The update lock is held until transaction commit, so draft writes/publication cannot
        // change the revision between this check and scenario persistence.
        return await db.ApplicationVersions.FromSqlInterpolated($"SELECT * FROM [dbo].[ApplicationVersions] WITH (UPDLOCK, HOLDLOCK) WHERE [ApplicationId] = {applicationId} AND [Status] = {VersionStatus.Draft.ToString()}")
            .AsTracking().SingleOrDefaultAsync(ct);
    }

    private static async Task LockApplicationAsync(QuestionAppBuilderDbContext db, int applicationId, CancellationToken ct)
    {
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM [dbo].[Applications] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {applicationId}")
            .AsTracking().SingleOrDefaultAsync(ct);
        if (application is null) throw new NotFoundException($"Application {applicationId} was not found.");
    }
}
