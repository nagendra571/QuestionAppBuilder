using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Repositories;

public sealed class ApplicationRepository : IApplicationRepository
{
    private readonly QuestionAppBuilderDbContext _context;

    public ApplicationRepository(QuestionAppBuilderDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public Task<ApplicationEntity?> GetAsync(int applicationId, CancellationToken ct = default)
        => _context.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct);

    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
        => _context.Applications.AnyAsync(a => a.Name == name, ct);

    public async Task<IReadOnlyList<ApplicationEntity>> ListAsync(CancellationToken ct = default)
        => await _context.Applications.OrderBy(a => a.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default)
        => await _context.ApplicationTypes.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);

    public Task<bool> ApplicationTypeNameExistsAsync(string name, CancellationToken ct = default)
        => _context.ApplicationTypes.AnyAsync(t => t.Name.ToUpper() == name.ToUpper(), ct);

    public async Task<ApplicationVersionGraph?> GetDraftGraphAsync(int applicationId, CancellationToken ct = default)
    {
        var version = await GraphsWithChildren()
            .Where(v => v.ApplicationId == applicationId && v.Status == VersionStatus.Draft)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);

        return version is null
            ? null
            : new ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions };
    }

    public async Task<ApplicationVersionGraph?> GetTrackedGraphByChildIdAsync(ChildEntityKind kind, int childId, CancellationToken ct = default)
    {
        var query = GraphsWithChildren();
        var matching = kind switch
        {
            ChildEntityKind.Category => query.Where(v => v.Categories.Any(c => c.Id == childId)),
            ChildEntityKind.Element => query.Where(v => v.Categories.SelectMany(c => c.Elements).Any(e => e.Id == childId)),
            ChildEntityKind.Question => query.Where(v => v.Categories.SelectMany(c => c.Elements).Any(e => e.Question != null && e.Question.Id == childId)),
            ChildEntityKind.Option => query.Where(v => v.Categories.SelectMany(c => c.Elements).Any(e => e.Question != null && e.Question.Options.Any(o => o.Id == childId))),
            ChildEntityKind.Rule => query.Where(v => v.Categories.SelectMany(c => c.Elements).Any(e => e.Question != null && e.Question.Rules.Any(r => r.Id == childId))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        var version = await matching.FirstOrDefaultAsync(ct);

        return version is null
            ? null
            : new ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions };
    }

    public async Task<ApplicationVersionGraph> GetVersionGraphAsync(int applicationVersionId, CancellationToken ct = default)
    {
        var version = await GraphsWithChildren()
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == applicationVersionId, ct);

        if (version is null)
        {
            throw new NotFoundException($"Application version {applicationVersionId} was not found.");
        }

        return new ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions };
    }

    public void Add(ApplicationEntity application) => _context.Applications.Add(application);

    public void Add(ApplicationType applicationType) => _context.ApplicationTypes.Add(applicationType);

    public void AddGraph(ApplicationVersionGraph graph) => _context.ApplicationVersions.Add(graph.Version);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    private IQueryable<ApplicationVersion> GraphsWithChildren() =>
        _context.ApplicationVersions
            .Include(v => v.Categories)
                .ThenInclude(c => c.Elements)
                    .ThenInclude(e => e.Question)
                        .ThenInclude(q => q!.Options)
            .Include(v => v.Categories)
                .ThenInclude(c => c.Elements)
                    .ThenInclude(e => e.Question)
                        .ThenInclude(q => q!.Rules)
                            .ThenInclude(r => r.Options)
            .Include(v => v.VisibilityConditions)
                .ThenInclude(condition => condition.Predicates)
                    .ThenInclude(predicate => predicate.Options);

}
