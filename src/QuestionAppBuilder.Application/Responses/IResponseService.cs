namespace QuestionAppBuilder.Application.Responses;

public interface IResponseService
{
    Task<ResponseLandingSnapshot?> GetLandingAsync(int applicationId, CancellationToken ct = default);
    Task<ResponseSnapshot> StartAsync(int applicationId, CancellationToken ct = default);
    Task<ResponseSnapshot> GetAsync(Guid responseId, CancellationToken ct = default);
    Task<ResponseSnapshot> SaveAsync(SaveResponseRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ResponseSummary>> ListMineAsync(int take, DateTime? beforeUtc = null, Guid? beforeId = null, CancellationToken ct = default);
}
