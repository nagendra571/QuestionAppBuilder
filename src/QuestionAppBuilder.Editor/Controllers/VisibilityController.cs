using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class VisibilityController : EditorControllerBase
{
    private readonly IApplicationDesignService _design;
    private readonly QuestionAppBuilder.Application.Authoring.IDraftService _drafts;
    public VisibilityController(QuestionAppBuilderEditorOptions options, IApplicationDesignService design,
        QuestionAppBuilder.Application.Authoring.IDraftService drafts) : base(options) { _design = design; _drafts = drafts; }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddElementCondition(int applicationId, int elementId, int sourceQuestionId,
        ConditionOperator conditionOperator, ConditionJunction junction, decimal? numberValue, int[]? optionIds,
        byte[]? expectedRevision, CancellationToken ct)
    {
        try
        {
            await _design.AddVisibilityPredicateAsync(elementId, sourceQuestionId, conditionOperator, junction, numberValue, optionIds,
                ct, expectedRevision);
            if (Request.Headers["X-Requested-With"] == "QuestionAppBuilder")
                return Json(new { success = true, revision = Convert.ToBase64String((await _drafts.GetAsync(applicationId, ct)).Revision) });
        }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception)
        {
            if (Request.Headers["X-Requested-With"] == "QuestionAppBuilder") return UnprocessableEntity(new { message = exception.Message });
            TempData["EditorError"] = exception.Message;
        }
        return RedirectToAction("Index", "Drafts", new { applicationId });
    }
}
