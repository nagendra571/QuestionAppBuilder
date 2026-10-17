using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class ApplicationTypesController : EditorControllerBase
{
    private readonly IApplicationDesignService _design;
    public ApplicationTypesController(QuestionAppBuilderEditorOptions options, IApplicationDesignService design) : base(options) => _design = design;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
        => View(new ApplicationTypesViewModel { Types = await _design.ListApplicationTypesAsync(ct) });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApplicationTypesViewModel model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model.NewName) || model.NewName.Trim().Length > 100)
            ModelState.AddModelError(nameof(model.NewName), "Enter a name up to 100 characters.");
        if (!ModelState.IsValid)
        {
            model.Types = await _design.ListApplicationTypesAsync(ct);
            return View("Index", model);
        }
        try { await _design.CreateApplicationTypeAsync(model.NewName, ct); }
        catch (DuplicateApplicationTypeNameException exception)
        {
            ModelState.AddModelError(nameof(model.NewName), exception.Message);
            model.Types = await _design.ListApplicationTypesAsync(ct);
            return View("Index", model);
        }
        return RedirectToAction(nameof(Index));
    }
}
