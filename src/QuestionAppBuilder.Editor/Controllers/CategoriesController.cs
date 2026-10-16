using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public class CategoriesController : EditorControllerBase
{
    private readonly IApplicationDesignService _designService;

    public CategoriesController(
        QuestionAppBuilderEditorOptions editorOptions,
        IApplicationDesignService designService)
        : base(editorOptions)
    {
        _designService = designService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(int applicationId, CategoryFormViewModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return RedirectToDetailWithValidationErrors(applicationId);
        }

        try
        {
            await _designService.AddCategoryAsync(applicationId, form.Name, form.IsIndependent, ct);
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
    public async Task<IActionResult> Update(int id, int applicationId, CategoryFormViewModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return RedirectToDetailWithValidationErrors(applicationId);
        }

        try
        {
            await _designService.UpdateCategoryAsync(id, form.Name, form.IsIndependent, ct);
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
            await _designService.RemoveCategoryAsync(id, ct);
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
