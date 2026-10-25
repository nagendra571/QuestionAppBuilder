using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Application.Authoring;

public static class PreviewScenarioLimits
{
    public const int MaximumNameLength = 200;
    public const int MaximumScenarios = 50;
    public const int MaximumAnswers = 500;
    public const int MaximumPayloadBytes = 256 * 1024;
}

/// <summary>Versioned persisted payload. Numeric strings retain invariant decimal precision.</summary>
public sealed record PreviewScenarioPayload(int SchemaVersion,
    IReadOnlyList<PreviewScenarioAnswer> Answers, bool? ExpectedValid, int? ExpectedTotal);

public sealed record PreviewScenarioAnswer(Guid ElementKey, AnswerType AnswerType,
    string? Text, string? Number, IReadOnlyList<Guid>? SelectedOptionKeys);

/// <summary>Current preview inputs; capture resolves database IDs to stable keys on the server.</summary>
public sealed record PreviewScenarioInput(string? Text, string? Number,
    IReadOnlyList<int>? SelectedOptionIds);

public sealed record PreviewScenarioDraft(int ApplicationId, int ExpectedVersionId, byte[] ExpectedRevision);

public sealed record SavePreviewScenarioRequest(PreviewScenarioDraft Draft, Guid? ScenarioId,
    byte[]? ExpectedScenarioRevision, string Name, IReadOnlyDictionary<int, PreviewScenarioInput> Answers,
    bool? ExpectedValid, int? ExpectedTotal);

public sealed record PreviewScenarioSummary(Guid Id, string Name, byte[] Revision,
    DateTime UpdatedUtc, bool? ExpectedValid, int? ExpectedTotal);

/// <summary>Load replaces the complete current preview answer set; issues prohibit a silent partial load.</summary>
public sealed record LoadedPreviewScenario(PreviewScenarioSummary Scenario, int VersionId, byte[] DraftRevision,
    IReadOnlyDictionary<int, PreviewScenarioInput> Answers, IReadOnlyList<string> RepairIssues);

public enum PreviewScenarioRunStatus { Passed, Failed, NeedsRepair }

public sealed record PreviewScenarioRunResult(Guid ScenarioId, string Name, byte[] ScenarioRevision,
    PreviewScenarioRunStatus Status, bool? ExpectedValid, int? ExpectedTotal,
    bool? ActualValid, int? ActualTotal, IReadOnlyList<string> Messages);

public sealed record PreviewScenarioRunReport(int VersionId, byte[] DraftRevision,
    IReadOnlyList<PreviewScenarioRunResult> Results);

public interface IPreviewScenarioService
{
    Task<IReadOnlyList<PreviewScenarioSummary>> ListAsync(int applicationId, CancellationToken ct = default);
    Task<PreviewScenarioSummary> SaveAsync(SavePreviewScenarioRequest request, CancellationToken ct = default);
    Task<LoadedPreviewScenario> LoadAsync(PreviewScenarioDraft draft, Guid scenarioId, CancellationToken ct = default);
    Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision, CancellationToken ct = default);

    /// <summary>
    /// Null scenarioId runs all cases against one current saved-draft snapshot with requireComplete:true.
    /// Broken references, unsupported payloads and definition errors produce NeedsRepair, never a pass.
    /// Running does not write scenarios, drafts, responses, submissions or publication state.
    /// </summary>
    Task<PreviewScenarioRunReport> RunAsync(PreviewScenarioDraft draft, Guid? scenarioId = null,
        CancellationToken ct = default);
}
