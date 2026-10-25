using QuestionAppBuilder.Application.Authoring;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed record RunPreviewScenarioRequest(PreviewScenarioDraft Draft, Guid? ScenarioId = null);
public sealed record DeletePreviewScenarioRequest(Guid ScenarioId, byte[] ExpectedScenarioRevision);
public sealed record LoadPreviewScenarioRequest(PreviewScenarioDraft Draft, Guid ScenarioId);
