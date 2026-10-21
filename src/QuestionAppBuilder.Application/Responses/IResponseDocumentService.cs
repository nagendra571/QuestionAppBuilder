namespace QuestionAppBuilder.Application.Responses;

public sealed record ResponseDocumentDownload(string FileName, byte[] Content);

public interface IResponseDocumentService
{
    const int MaximumBytes = 10 * 1024 * 1024;
    Task<ResponseSnapshot> UploadAsync(Guid responseId, int questionId, byte[] expectedRevision, string fileName, Stream content, CancellationToken ct = default);
    Task<ResponseSnapshot> RemoveAsync(Guid responseId, int questionId, byte[] expectedRevision, CancellationToken ct = default);
    Task<ResponseDocumentDownload?> DownloadAsync(Guid responseId, int questionId, CancellationToken ct = default);
    // Caller must enforce the same authorization as submission reports.
    Task<ResponseDocumentDownload?> DownloadSubmissionAsync(int submissionId, int questionId, CancellationToken ct = default);
}
