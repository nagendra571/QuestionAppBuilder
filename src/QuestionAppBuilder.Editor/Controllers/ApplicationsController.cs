using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public class ApplicationsController : EditorControllerBase
{
    private readonly IApplicationDesignService _designService;

    public ApplicationsController(
        QuestionAppBuilderEditorOptions editorOptions,
        IApplicationDesignService designService)
        : base(editorOptions)
    {
        _designService = designService;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var applications = await _designService.ListApplicationsAsync(ct);
        var types = await _designService.ListApplicationTypesAsync(ct);
        var typeNames = types.ToDictionary(type => type.Id, type => type.Name);

        var rows = applications
            .Select(application => new ApplicationRowViewModel
            {
                Id = application.Id,
                Name = application.Name,
                TypeName = typeNames.TryGetValue(application.ApplicationTypeId, out var typeName)
                    ? typeName
                    : string.Empty,
            })
            .ToList();

        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var viewModel = new ApplicationCreateViewModel
        {
            Types = await _designService.ListApplicationTypesAsync(ct),
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApplicationCreateViewModel viewModel, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return await RerenderCreateAsync(viewModel, ct);
        }

        try
        {
            var application = await _designService.CreateApplicationAsync(viewModel.Name, viewModel.ApplicationTypeId, ct);
            return RedirectToAction("Detail", new { id = application.Id });
        }
        catch (DuplicateApplicationNameException exception)
        {
            ModelState.AddModelError(nameof(viewModel.Name), exception.Message);
        }
        catch (DefinitionValidationException exception)
        {
            ModelState.AddModelError(nameof(viewModel.Name), exception.Message);
        }

        return await RerenderCreateAsync(viewModel, ct);
    }

    private async Task<IActionResult> RerenderCreateAsync(ApplicationCreateViewModel viewModel, CancellationToken ct)
    {
        viewModel.Types = await _designService.ListApplicationTypesAsync(ct);
        return View(viewModel);
    }
}
