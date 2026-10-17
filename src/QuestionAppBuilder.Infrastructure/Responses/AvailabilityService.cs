using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Responses;

public sealed class AvailabilityService : IAvailabilityService
{
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;
    private readonly ITrustedActorContext _actor;
    public AvailabilityService(IDbContextFactory<QuestionAppBuilderDbContext> factory, ITrustedActorContext actor) { _factory = factory; _actor = actor; }

    public async Task SetAsync(AvailabilityUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.ExpectedRevision);
        var actorKey = _actor.GetRequiredActorKey();
        if (string.IsNullOrWhiteSpace(actorKey) || actorKey.Length > 256) throw new InvalidOperationException("A trusted actor identity is required.");
        await using var strategyContext = await _factory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await SqlApplicationLock.AcquireAsync(db, update.ApplicationId, cancellationToken);
            var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == update.ApplicationId, cancellationToken)
                ?? throw new NotFoundException($"Application {update.ApplicationId} was not found.");
            if (!application.RowVersion.AsSpan().SequenceEqual(update.ExpectedRevision)) throw new ResponseConflictException();
            application.AcceptSubmissions = update.AcceptSubmissions;
            application.AcceptNewResponses = update.AcceptSubmissions && update.AcceptNewResponses;
            application.UpdatedUtc = DateTime.UtcNow;
            db.ApplicationAuditEvents.Add(new ApplicationAuditEvent { ApplicationId = application.Id, ApplicationVersionId = application.CurrentVersionId,
                ActorKey = actorKey, EventType = "AvailabilityChanged", OccurredUtc = application.UpdatedUtc });
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException ex) { throw new ResponseConflictException(ex); }
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
