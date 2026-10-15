using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Domain.Interfaces;

public interface IApplicationRepository
{
    Task<Application?> GetAsync(int applicationId, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Application>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default);
    Task<ApplicationVersionGraph?> GetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetVersionGraphAsync(int applicationVersionId, CancellationToken ct = default);
    void Add(Application application);
    void AddGraph(ApplicationVersionGraph graph);
    Task SaveChangesAsync(CancellationToken ct = default);
}
