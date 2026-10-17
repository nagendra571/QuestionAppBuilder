namespace QuestionAppBuilder.Application.Responses;

public interface ISubmissionCoordinator
{
    Task<SubmissionReceipt> SubmitAsync(SubmitResponseRequest request, CancellationToken cancellationToken = default);
    Task<SubmissionReceipt> GetReceiptAsync(Guid responseId, CancellationToken cancellationToken = default);
}
