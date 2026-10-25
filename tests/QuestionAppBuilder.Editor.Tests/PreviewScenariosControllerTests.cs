using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Controllers;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class PreviewScenariosControllerTests
{
    [Fact]
    public void Scenario_routes_are_designer_protected_and_posts_require_antiforgery()
    {
        typeof(PreviewScenariosController).GetCustomAttribute<ServiceFilterAttribute>(inherit: true).Should().NotBeNull();
        foreach (var methodName in new[] { "Save", "Load", "Run", "Delete" })
            typeof(PreviewScenariosController).GetMethod(methodName)!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        typeof(PreviewScenariosController).GetMethod("List")!.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Run_serializes_status_as_strings_and_rejects_cross_application_requests()
    {
        var service = new ScenarioServiceStub();
        var controller = new PreviewScenariosController(new QuestionAppBuilderEditorOptions(), service);
        var revision = new byte[] { 3, 4, 5 };
        service.RunReport = new PreviewScenarioRunReport(91, revision,
            [new(Guid.NewGuid(), "A case", [1], PreviewScenarioRunStatus.NeedsRepair, true, 8, null, null, ["Fix the scenario."])]);

        var mismatch = await controller.Run(73, new RunPreviewScenarioRequest(new(74, 91, revision)), CancellationToken.None);
        mismatch.Should().BeOfType<BadRequestObjectResult>();
        service.RunCalls.Should().Be(0);

        var result = await controller.Run(73, new RunPreviewScenarioRequest(new(73, 91, revision)), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result.Should().BeOfType<JsonResult>().Subject.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("NeedsRepair");
        json.GetProperty("draftRevision").GetString().Should().Be(Convert.ToBase64String(revision));
        service.RunCalls.Should().Be(1);
    }

    [Fact]
    public async Task Scenario_revision_conflicts_are_returned_as_conflicts()
    {
        var service = new ScenarioServiceStub { ThrowDraftConflict = true };
        var controller = new PreviewScenariosController(new QuestionAppBuilderEditorOptions(), service);
        var request = new RunPreviewScenarioRequest(new(73, 91, new byte[] { 1 }));

        var result = await controller.Run(73, request, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    private sealed class ScenarioServiceStub : IPreviewScenarioService
    {
        public PreviewScenarioRunReport RunReport { get; set; } = new(91, [], []);
        public int RunCalls { get; private set; }
        public bool ThrowDraftConflict { get; init; }
        public Task<IReadOnlyList<PreviewScenarioSummary>> ListAsync(int applicationId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PreviewScenarioSummary>>([]);
        public Task<PreviewScenarioSummary> SaveAsync(SavePreviewScenarioRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LoadedPreviewScenario> LoadAsync(PreviewScenarioDraft draft, Guid scenarioId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<PreviewScenarioRunReport> RunAsync(PreviewScenarioDraft draft, Guid? scenarioId = null, CancellationToken ct = default)
        {
            RunCalls++;
            if (ThrowDraftConflict) throw new DraftConflictException();
            return Task.FromResult(RunReport);
        }
    }
}
