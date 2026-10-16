using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public class ElementsController : EditorControllerBase
{
    private readonly IApplicationDesignService _designService;

    public ElementsController(
        QuestionAppBuilderEditorOptions editorOptions,
        IApplicationDesignService designService)
        : base(editorOptions)
    {
        _designService = designService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContent(int id, int applicationId, ContentElementFormViewModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return RedirectToDetailWithValidationErrors(applicationId);
        }

        try
        {
            await _designService.AddContentElementAsync(id, form.Label, form.HelpText, ct);
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
    public async Task<IActionResult> AddQuestion(int id, int applicationId, QuestionFormViewModel form, CancellationToken ct)
    {
        if (!Enum.IsDefined(form.AnswerType))
        {
            ModelState.AddModelError(nameof(form.AnswerType), "Select an answer type.");
        }

        if (!ModelState.IsValid)
        {
            return RedirectToDetailWithValidationErrors(applicationId);
        }

        try
        {
            await _designService.AddQuestionAsync(id, form.Label, form.AnswerType, form.IsRequired, form.HelpText, ct);
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
            await _designService.RemoveElementAsync(id, ct);
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

    private IActionResult RedirectToDetail(int applicationId)
        => RedirectToAction("Detail", "Applications", new { id = applicationId });

    private IActionResult RedirectToDetailWithValidationErrors(int applicationId)
    {
        var messages = ModelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct();

        SetEditorError(string.Join(" ", messages));
        return RedirectToDetail(applicationId);
    }
}
