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

    [HttpGet]
    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        try
        {
            var application = await _designService.GetApplicationAsync(id, ct);
            var draftGraph = await _designService.TryGetDraftGraphAsync(id, ct);
            var publishedGraph = draftGraph is null
                ? await _designService.GetLatestPublishedGraphAsync(id, ct)
                : null;
            var types = await _designService.ListApplicationTypesAsync(ct);

            var viewModel = new ApplicationDetailViewModel
            {
                Application = application,
                DraftGraph = draftGraph,
                PublishedGraph = publishedGraph,
                TypeName = types.FirstOrDefault(type => type.Id == application.ApplicationTypeId)?.Name ?? string.Empty,
            };

            return View(viewModel);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartDraft(int id, CancellationToken ct)
    {
        try
        {
            await _designService.OpenDraftAsync(id, ct);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (DefinitionValidationException exception)
        {
            SetEditorError(exception.Message);
        }

        return RedirectToAction("Detail", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id, CancellationToken ct)
    {
        try
        {
            await _designService.PublishAsync(id, ct);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (DefinitionValidationException exception)
        {
            SetEditorError(exception.Message);
        }

        return RedirectToAction("Detail", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApplicationCreateViewModel viewModel, CancellationToken ct)
    {
        viewModel.Types = await _designService.ListApplicationTypesAsync(ct);

        if (!viewModel.Types.Any(type => type.Id == viewModel.ApplicationTypeId))
        {
            ModelState.AddModelError(nameof(viewModel.ApplicationTypeId), "Select an application type.");
        }

        if (!ModelState.IsValid)
        {
            return View(viewModel);
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

        return View(viewModel);
    }
}
