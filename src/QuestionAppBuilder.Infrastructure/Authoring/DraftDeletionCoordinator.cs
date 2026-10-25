using System.Data;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Authoring;

public sealed class DraftDeletionCoordinator(IDbContextFactory<QuestionAppBuilderDbContext> factory) : IDraftDeletionCoordinator
{
    public Task<DraftDeletionPreview> PreviewAsync(DraftDeletionTarget target, byte[] expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        return ExecuteAsync(async db =>
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var graph = await LoadDraftAsync(db, target.ApplicationId, tracking: false, ct);
            EnsureRevision(graph, expectedRevision);
            FindTarget(graph, target.Kind, target.TargetId);
            var preview = DraftDeletionImpactAnalyzer.Analyze(graph, target.Kind, target.TargetId);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return preview;
        }, ct);
    }

    public async Task ConfirmAsync(ConfirmDraftDeletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedRevision);
        await ExecuteAsync(async db =>
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct) : null;
            var graph = await LoadDraftAsync(db, request.ApplicationId, tracking: true, ct);
            if (graph.Version.Id != request.ExpectedVersionId) throw new DraftConflictException();
            EnsureRevision(graph, request.ExpectedRevision);
            var target = FindTarget(graph, request.Kind, request.TargetId);
            if (target.LogicalKey != request.TargetLogicalKey)
                throw new NotFoundException("The deletion target no longer matches this draft.");
            var preview = DraftDeletionImpactAnalyzer.Analyze(graph, request.Kind, request.TargetId);
            if (!preview.CanDelete)
                throw new DefinitionValidationException(string.Join(" ", preview.Blocked.Select(item => item.Detail).Concat(preview.Warnings)));
            DraftDeletionGraph.Apply(graph, request.Kind, request.TargetId);
            graph.Version.RevisionNonce = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return true;
        }, ct);
    }

    private async Task<T> ExecuteAsync<T>(Func<QuestionAppBuilderDbContext, Task<T>> operation, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var fresh = await factory.CreateDbContextAsync(ct);
                return await operation(fresh);
            });
        }
        catch (DbUpdateConcurrencyException) { throw new DraftConflictException(); }
    }

    private static async Task<ApplicationVersionGraph> LoadDraftAsync(QuestionAppBuilderDbContext db, int applicationId, bool tracking, CancellationToken ct)
    {
        IQueryable<ApplicationVersion> query = db.ApplicationVersions
            .Include(version => version.Categories).ThenInclude(category => category.Elements).ThenInclude(element => element.Question).ThenInclude(question => question!.Options)
            .Include(version => version.Categories).ThenInclude(category => category.Elements).ThenInclude(element => element.Question).ThenInclude(question => question!.Rules).ThenInclude(rule => rule.Options)
            .Include(version => version.VisibilityConditions).ThenInclude(condition => condition.Predicates).ThenInclude(predicate => predicate.Options)
            .Where(version => version.ApplicationId == applicationId && version.Status == VersionStatus.Draft);
        if (!tracking) query = query.AsNoTracking();
        var version = await query.AsSplitQuery().SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException($"No draft version found for application {applicationId}.");
        return new ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions };
    }

    private static DraftDeletionEntity FindTarget(ApplicationVersionGraph graph, DraftDeletionKind kind, int targetId)
        => DraftDeletionGraph.FindTarget(graph, kind, targetId)
            ?? throw new NotFoundException("The deletion target is not in this draft.");

    private static void EnsureRevision(ApplicationVersionGraph graph, byte[] expected)
    {
        if (!graph.Version.RevisionNonce.ToByteArray().AsSpan().SequenceEqual(expected)) throw new DraftConflictException();
    }
}
