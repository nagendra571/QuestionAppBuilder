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
    public async Task<IActionResult> Add(int id, int applicationId, RuleFormViewModel form, CancellationToken ct)
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

        try
        {
            switch (form.Family)
            {
                case RuleFamily.Selection:
                    await _designService.AddRuleAsync(id, form.Family, form.Points, form.MembershipOperator, form.OptionIds, ct: ct);
                    break;
                case RuleFamily.NumericRange:
                    await _designService.AddRuleAsync(id, form.Family, form.Points, minValue: form.MinValue, maxValue: form.MaxValue, ct: ct);
                    break;
                default:
                    await _designService.AddRuleAsync(id, form.Family, form.Points, ct: ct);
                    break;
            }
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int id, int applicationId, CancellationToken ct)
    {
        try
        {
            await _designService.RemoveRuleAsync(id, ct);
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
