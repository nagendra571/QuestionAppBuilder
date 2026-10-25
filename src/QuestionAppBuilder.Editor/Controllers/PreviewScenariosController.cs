using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class PreviewScenariosController : EditorControllerBase
{
    private readonly IPreviewScenarioService _scenarios;

    public PreviewScenariosController(QuestionAppBuilderEditorOptions options, IPreviewScenarioService scenarios)
        : base(options) => _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> List(int id, CancellationToken ct)
    {
        try { return Json(await _scenarios.ListAsync(id, ct)); }
        catch (NotFoundException) { return NotFound(); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, [FromBody] SavePreviewScenarioRequest? request, CancellationToken ct)
    {
        if (request?.Draft is null || request.Draft.ApplicationId != id) return BadRequest(new { message = "Scenario request does not match this application." });
        try { return Json(await _scenarios.SaveAsync(request, ct)); }
        catch (DraftConflictException exception) { return Conflict(new { message = exception.Message }); }
        catch (PreviewScenarioConflictException exception) { return Conflict(new { message = exception.Message }); }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Load(int id, [FromBody] LoadPreviewScenarioRequest? request, CancellationToken ct)
    {
        if (request?.Draft is null || request.Draft.ApplicationId != id) return BadRequest(new { message = "Scenario request does not match this application." });
        try { return Json(await _scenarios.LoadAsync(request.Draft, request.ScenarioId, ct)); }
        catch (DraftConflictException exception) { return Conflict(new { message = exception.Message }); }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(int id, [FromBody] RunPreviewScenarioRequest? request, CancellationToken ct)
    {
        if (request?.Draft is null || request.Draft.ApplicationId != id) return BadRequest(new { message = "Scenario request does not match this application." });
        try
        {
            var report = await _scenarios.RunAsync(request.Draft, request.ScenarioId, ct);
            return Json(new
            {
                report.VersionId,
                DraftRevision = Convert.ToBase64String(report.DraftRevision),
                Results = report.Results.Select(result => new
                {
                    result.ScenarioId, result.Name, ScenarioRevision = Convert.ToBase64String(result.ScenarioRevision),
                    Status = result.Status.ToString(), result.ExpectedValid, result.ExpectedTotal,
                    result.ActualValid, result.ActualTotal, result.Messages
                })
            });
        }
        catch (DraftConflictException exception) { return Conflict(new { message = exception.Message }); }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, [FromBody] DeletePreviewScenarioRequest? request, CancellationToken ct)
    {
        if (request is null || request.ScenarioId == Guid.Empty || request.ExpectedScenarioRevision is null)
            return BadRequest(new { message = "Scenario deletion request is incomplete." });
        try
        {
            await _scenarios.DeleteAsync(id, request.ScenarioId, request.ExpectedScenarioRevision, ct);
            return Ok(new { deleted = true });
        }
        catch (PreviewScenarioConflictException exception) { return Conflict(new { message = exception.Message }); }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }
}
