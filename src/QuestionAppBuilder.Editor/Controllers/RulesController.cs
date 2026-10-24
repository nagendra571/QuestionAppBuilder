using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public class RulesController : EditorControllerBase
{
    private readonly IApplicationDesignService _designService;

    public RulesController(
        QuestionAppBuilderEditorOptions editorOptions,
        IApplicationDesignService designService)
        : base(editorOptions)
    {
        _designService = designService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(int id, int applicationId, byte[]? expectedRevision, RuleFormViewModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return RedirectToDetailWithValidationErrors(applicationId);
        }

        if (!Enum.IsDefined(form.Family))
        {
            SetEditorError("Select a rule family.");
            return RedirectToDetail(applicationId);
        }

        if (form.MembershipOperator is not null && !Enum.IsDefined(form.MembershipOperator.Value))
        {
            SetEditorError("Select a membership operator.");
            return RedirectToDetail(applicationId);
        }

        var added = false;
        try
        {
            switch (form.Family)
            {
                case RuleFamily.Selection:
                    await _designService.AddRuleWithAwardMethodAsync(id, form.Family, form.Points, form.MembershipOperator, form.OptionIds,
                        null, null, form.AwardMethod, form.PointsPerUnit, form.MaximumPoints, ct, expectedRevision);
                    break;
                case RuleFamily.NumericRange:
                    await _designService.AddRuleWithAwardMethodAsync(id, form.Family, form.Points, null, null,
                        form.MinValue, form.MaxValue, form.AwardMethod, form.PointsPerUnit, form.MaximumPoints, ct, expectedRevision);
                    break;
                default:
                    await _designService.AddRuleWithAwardMethodAsync(id, form.Family, form.Points, null, null,
                        null, null, form.AwardMethod, form.PointsPerUnit, form.MaximumPoints, ct, expectedRevision);
                    break;
            }
            added = true;
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (DefinitionValidationException exception)
        {
            SetEditorError(exception.Message);
        }

        if (added) TempData["AuthoringClose"] = $"add-rule-{id}";
        return RedirectToDetail(applicationId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int id, int applicationId, byte[]? expectedRevision, CancellationToken ct)
    {
        try
        {
            await _designService.RemoveRuleAsync(id, ct, expectedRevision);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (DefinitionValidationException exception)
        {
            SetEditorError(exception.Message);
        }

        return RedirectToDetail(applicationId);
    }
}
