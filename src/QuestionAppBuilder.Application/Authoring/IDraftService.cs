namespace QuestionAppBuilder.Application.Authoring;

public interface IDraftService
{
    Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default);
    async Task<DraftWorkspaceSnapshot> GetWorkspaceAsync(int applicationId, CancellationToken ct = default)
    {
        var draft = await GetAsync(applicationId, ct);
        var review = await ReviewAsync(applicationId, ct);
        return new DraftWorkspaceSnapshot(draft, review);
    }
    Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default);
    Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default);
    Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default);
}
