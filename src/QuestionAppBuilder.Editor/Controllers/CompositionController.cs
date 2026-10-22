using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class CompositionController : EditorControllerBase
{
    private readonly IDraftService _drafts;
    private readonly IDefinitionEvaluator _evaluator;

    public CompositionController(QuestionAppBuilderEditorOptions options, IDraftService drafts)
        : this(options, drafts, new DefinitionEvaluator()) { }

    [ActivatorUtilitiesConstructor]
    public CompositionController(QuestionAppBuilderEditorOptions options, IDraftService drafts, IDefinitionEvaluator evaluator)
        : base(options) { _drafts = drafts; _evaluator = evaluator; }

    [HttpGet]
    public async Task<IActionResult> Index(int id, CancellationToken ct)
    {
        try
        {
            var snapshot = await _drafts.GetAsync(id, ct);
            CompositionDefinition composition;
            var recovery = false;
            try
            {
                composition = CompositionDefinition.Read(snapshot.Definition.Version.CompositionJson);
                recovery = !IsStructurallyEditable(composition);
            }
            catch (System.Text.Json.JsonException)
            {
                composition = new CompositionDefinition();
                recovery = true;
            }

            if (recovery) composition = new CompositionDefinition();
            ViewData["CompositionDefinitionJson"] = composition.ToJson();
            ViewData["CompositionRecovery"] = recovery;
            return View(snapshot);
        }
        catch (NotFoundException) { return NotFound(); }
    }

    private static bool IsStructurallyEditable(CompositionDefinition definition) =>
        definition.Groups is { Count: <= 100 } groups
        && definition.Calculations is { Count: <= 200 } calculations
        && definition.Rules is { Count: <= 500 } rules
        && groups.All(group => group is not null && group.Columns is { Count: <= 8 } columns
            && columns.All(column => column is not null)
            && group.Rows is { Count: <= 200 } rows
            && rows.All(row => row is not null && row.Cells is not null && row.Cells.All(cell => cell is not null)))
        && calculations.All(calculation => calculation is not null && calculation.Sources is { Count: <= 200 }
            && calculation.Sources.All(source => source is not null))
        && rules.All(rule => rule is not null && rule.Checks is { Count: <= 50 }
            && rule.Conditions is { Count: <= 50 }
            && rule.Checks.All(check => check is not null && check.Left is not null && check.Right is not null)
            && rule.Conditions.All(check => check is not null && check.Left is not null && check.Right is not null))
        && definition.DynamicText is { Variables: { Count: <= 200 } variables, Templates: { Count: <= 500 } templates }
        && variables.All(variable => variable is not null && variable.Sources is { Count: <= 100 } sources
            && sources.All(source => source is not null))
        && templates.All(template => template is not null && template.Parts is { Count: <= 100 } parts
            && parts.All(part => part is not null && (part.Kind != DynamicPartKind.Value || part.Source is not null)));

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Save(int id, byte[] expectedRevision, string definitionJson, CancellationToken ct)
    {
        if (!ModelState.IsValid || expectedRevision is null || expectedRevision.Length == 0 || definitionJson?.Length > 500000 || definitionJson is null)
            return BadRequest("Configuration or revision is invalid.");
        try
        {
            var snapshot = await _drafts.GetAsync(id, ct);
            var composition = CompositionDefinition.Read(definitionJson);
            if (!IsStructurallyEditable(composition))
                return BadRequest(new { message = "Configuration exceeds supported limits or contains missing dynamic text fields." });
            snapshot.Definition.Version.CompositionJson = composition.ToJson();
            var result = _evaluator.Evaluate(snapshot.Definition, new Dictionary<int, AnswerValue>(), requireComplete: false);
            if (result.DefinitionIssues.Count != 0)
                return UnprocessableEntity(new { message = string.Join(" ", result.DefinitionIssues.Select(issue => issue.Message).Distinct()) });
            var saved = await _drafts.SaveAsync(new(id, expectedRevision, snapshot.Definition), ct);
            return Json(new { revision = Convert.ToBase64String(saved.Revision), message = "Configuration saved." });
        }
        catch (DraftConflictException) { return Conflict(new { message = "This draft changed elsewhere. Reload before saving; your edits are still shown." }); }
        catch (NotFoundException) { return NotFound(); }
        catch (System.Text.Json.JsonException) { return BadRequest(new { message = "The configuration could not be read." }); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }
}
