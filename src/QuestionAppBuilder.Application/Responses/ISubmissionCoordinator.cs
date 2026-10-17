namespace QuestionAppBuilder.Application.Responses;

public interface ISubmissionCoordinator
{
    Task<SubmissionReceipt> SubmitAsync(SubmitResponseRequest request, CancellationToken cancellationToken = default);
}
