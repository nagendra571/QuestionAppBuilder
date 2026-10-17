namespace QuestionAppBuilder.Application.Authoring;

public interface IDraftService
{
    Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default);
    Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default);
    Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default);
    Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default);
}
