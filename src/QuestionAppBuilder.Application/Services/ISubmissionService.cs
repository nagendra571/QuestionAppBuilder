using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Application.Services;

/// <summary>Legacy unowned submission API retained for source compatibility; it is intentionally not registered by the platform.</summary>
[Obsolete("Use ISubmissionCoordinator with an owned, persisted response.")]
public interface ISubmissionService
{
    Task<Submission> SubmitAsync(SubmitRequest request, CancellationToken ct = default);
}
