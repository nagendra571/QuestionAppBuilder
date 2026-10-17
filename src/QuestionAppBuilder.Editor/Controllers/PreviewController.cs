using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using QuestionAppBuilder.Domain.Enums;
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
    private readonly ICompositeViewEngine _viewEngine;
    public PreviewController(QuestionAppBuilderEditorOptions options, IDraftService drafts, IDefinitionEvaluator evaluator,
        IQuestionnaireRenderer renderer, ICompositeViewEngine viewEngine)
        : base(options) { _drafts = drafts; _evaluator = evaluator; _renderer = renderer; _viewEngine = viewEngine; }

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
                var answerType = snapshot.Definition.Categories.SelectMany(category => category.Elements)
                    .FirstOrDefault(element => element.Question?.Id == questionId)?.Question?.AnswerType;
                var selections = input.SelectedOptionIds ?? new List<int>();
                var selectedOptionIds = answerType is AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes
                    ? selections
                    : selections.Count == 0 ? null : selections;
                answers[questionId] = new AnswerValue(input.Text, number, selectedOptionIds);
            }
            var checkCompleteness = request.Submit || request.Review
                || request.TargetCategoryIndex is int requestedTarget && requestedTarget > request.CategoryIndex;
            var result = _evaluator.Evaluate(snapshot.Definition, answers, requireComplete: checkCompleteness);
            var model = _renderer.Create(snapshot.Definition, answers, result, preview: true);
            var categoryIndex = Math.Clamp(request.TargetCategoryIndex ?? request.CategoryIndex, 0, Math.Max(0, model.Categories.Count - 1));
            var fieldErrors = result.AnswerIssues.Where(issue => issue.QuestionId is not null)
                .GroupBy(issue => issue.QuestionId!.Value).ToDictionary(group => group.Key, group => group.First().Message);
            string? summary = null;
            var positions = model.Categories.SelectMany(category => category.Elements.Where(element => element.Question is not null)
                .Select(element => (element.Question!.Id, category.Index))).ToDictionary(item => item.Id, item => item.Index);
            if (request.TargetCategoryIndex is int target && target > request.CategoryIndex
                && snapshot.Definition.Version.NavigationMode == NavigationMode.Sequential)
            {
                var firstIncomplete = result.AnswerIssues.Where(issue => issue.QuestionId is int id
                    && positions.TryGetValue(id, out var position) && position < target)
                    .Select(issue => positions[issue.QuestionId!.Value]).DefaultIfEmpty(target).Min();
                if (firstIncomplete < target)
                {
                    categoryIndex = firstIncomplete;
                    summary = "Complete the required fields before continuing.";
                }
            }
            if (request.Review && snapshot.Definition.Version.NavigationMode == NavigationMode.Sequential)
            {
                var firstIncomplete = result.AnswerIssues.Where(issue => issue.QuestionId is int id && positions.ContainsKey(id))
                    .Select(issue => positions[issue.QuestionId!.Value]).DefaultIfEmpty(categoryIndex).Min();
                if (result.AnswerIssues.Any(issue => issue.QuestionId is int id && positions.ContainsKey(id)))
                {
                    categoryIndex = firstIncomplete;
                    summary = "Complete the required fields before reviewing your answers.";
                }
            }
            var page = new QuestionAppBuilder.Core.ViewModels.RuntimePreviewPageModel(model, categoryIndex,
                snapshot.Definition.Version.NavigationMode, snapshot.Definition.Version.ResultVisibility,
                fieldErrors, summary, request.Review && summary is null, false, result.Score, result.DefinitionIssues);
            string html;
            if (request.Submit && result.AnswerIssues.Count == 0 && result.DefinitionIssues.Count == 0)
            {
                page = page with { IsReceipt = true, IsReview = false };
                html = await RenderPartialAsync("_RuntimePreviewReceipt", page);
            }
            else if (request.Submit || request.Review && summary is null)
            {
                page = page with { IsReview = true };
                html = await RenderPartialAsync("_RuntimePreviewReview", page);
            }
            else html = await RenderPartialAsync("_RuntimePreview", page);
            return Json(new PreviewResponse(model, result.Score, result.AnswerIssues, result.DefinitionIssues)
            {
                Html = html,
                NavigationMode = snapshot.Definition.Version.NavigationMode,
                ResultVisibility = snapshot.Definition.Version.ResultVisibility,
                CategoryIndex = categoryIndex,
                IsReceipt = page.IsReceipt
            });
        }
        catch (QuestionAppBuilder.Domain.Exceptions.NotFoundException) { return NotFound(); }
    }

    private async Task<string> RenderPartialAsync(string viewName, object model)
    {
        var result = _viewEngine.GetView(null, $"~/Areas/QuestionAppBuilderCore/Views/Shared/{viewName}.cshtml", isMainPage: false);
        if (!result.Success) throw new InvalidOperationException($"The runtime preview view '{viewName}' could not be located.");
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), ModelState) { Model = model };
        using var writer = new StringWriter();
        await result.View.RenderAsync(new ViewContext(ControllerContext, result.View, viewData, TempData, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }
}
