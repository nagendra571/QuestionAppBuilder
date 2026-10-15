using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Domain.Interfaces;

public interface ISubmissionRepository
{
    Task AddAsync(Submission submission, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
