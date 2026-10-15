using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Application.Services;

public interface ISubmissionService
{
    Task<Submission> SubmitAsync(SubmitRequest request, CancellationToken ct = default);
}
