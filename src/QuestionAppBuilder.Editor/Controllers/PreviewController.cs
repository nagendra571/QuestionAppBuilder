using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Core.Rendering;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class PreviewController : EditorControllerBase
{
    private readonly IDraftService _drafts; private readonly IDefinitionEvaluator _evaluator; private readonly IQuestionnaireRenderer _renderer;
    public PreviewController(QuestionAppBuilderEditorOptions options, IDraftService drafts, IDefinitionEvaluator evaluator, IQuestionnaireRenderer renderer)
        : base(options) { _drafts = drafts; _evaluator = evaluator; _renderer = renderer; }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(int applicationId, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        try
        {
            var snapshot = await _drafts.GetAsync(applicationId, ct);
            var answers = new Dictionary<int, AnswerValue>();
            foreach (var (questionId, input) in request.Answers)
            {
                decimal? number = null;
                if (!string.IsNullOrWhiteSpace(input.Number))
                {
                    if (!decimal.TryParse(input.Number, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        return UnprocessableEntity(new { message = $"Question {questionId} needs a valid number." });
                    number = parsed;
                }
                answers[questionId] = new AnswerValue(input.Text, number, input.SelectedOptionIds);
            }
            var result = _evaluator.Evaluate(snapshot.Definition, answers, requireComplete: false);
            var model = _renderer.Create(snapshot.Definition, answers, result, preview: true);
            return Json(new PreviewResponse(model, result.Score, result.AnswerIssues, result.DefinitionIssues));
        }
        catch (QuestionAppBuilder.Domain.Exceptions.NotFoundException) { return NotFound(); }
    }
}
