using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public class OptionsController : EditorControllerBase
{
    private static readonly AnswerType[] ChoiceAnswerTypes =
    {
        AnswerType.Dropdown,
        AnswerType.RadioGroup,
        AnswerType.Checkboxes,
    };

    private readonly IApplicationDesignService _designService;

    public OptionsController(
        QuestionAppBuilderEditorOptions editorOptions,
        IApplicationDesignService designService)
        : base(editorOptions)
    {
        _designService = designService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(int id, int applicationId, OptionFormViewModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return RedirectToDetailWithValidationErrors(applicationId);
        }

        var question = await FindDraftQuestionAsync(id, applicationId, ct);
        if (question is null)
        {
            return NotFound();
        }

        if (!ChoiceAnswerTypes.Contains(question.AnswerType))
        {
            SetEditorError("Options can only be added to choice questions (dropdown, radio group, or checkboxes).");
            return RedirectToDetail(applicationId);
        }

        try
        {
            await _designService.AddOptionAsync(id, form.Label, form.Value, ct);
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
            await _designService.RemoveOptionAsync(id, ct);
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

    private async Task<Question?> FindDraftQuestionAsync(int questionId, int applicationId, CancellationToken ct)
    {
        var draft = await _designService.TryGetDraftGraphAsync(applicationId, ct);
        return draft?.Categories
            .SelectMany(category => category.Elements)
            .Select(element => element.Question)
            .FirstOrDefault(question => question is not null && question.Id == questionId);
    }
}
