namespace QuestionAppBuilder.Application.Responses;

public interface ISubmissionCoordinator
{
    Task<SubmissionReceipt> SubmitAsync(SubmitResponseRequest request, CancellationToken cancellationToken = default);
    Task<SubmissionReceipt> GetReceiptAsync(Guid responseId, CancellationToken cancellationToken = default);
    async Task<SubmissionReceipt> GetSubmittedApplicationAsync(Guid responseId, CancellationToken cancellationToken = default)
    {
        var receipt = await GetReceiptAsync(responseId, cancellationToken);
        return receipt with { Total = null, Categories = null, SubmittedApplication = null };
    }
}
