using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Domain.Interfaces;

public interface IPreviewScenarioRepository
{
    Task<IReadOnlyList<PreviewScenario>> ListAsync(int applicationId, CancellationToken ct = default);
    Task<PreviewScenario?> GetAsync(int applicationId, Guid scenarioId, CancellationToken ct = default);

    /// <summary>
    /// Null expectedScenarioRevision creates; a non-null revision updates an existing scenario.
    /// Atomically checks application membership, the current draft version/revision, the scenario
    /// revision and the application scenario-count limit before writing. Never creates a draft.
    /// </summary>
    Task<PreviewScenario> SaveAsync(PreviewScenario scenario, int expectedVersionId,
        byte[] expectedDraftRevision, byte[]? expectedScenarioRevision, CancellationToken ct = default);

    Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision,
        CancellationToken ct = default);
}
