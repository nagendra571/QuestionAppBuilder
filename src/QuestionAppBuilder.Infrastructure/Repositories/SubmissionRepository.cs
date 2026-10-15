using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Interfaces;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Repositories;

public sealed class SubmissionRepository : ISubmissionRepository
{
    private readonly QuestionAppBuilderDbContext _context;

    public SubmissionRepository(QuestionAppBuilderDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task AddAsync(Submission submission, CancellationToken ct = default)
    {
        _context.Submissions.Add(submission);
        await SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
