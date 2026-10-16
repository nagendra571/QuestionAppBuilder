using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Services;
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
        var rows = applications
            .Select(application => new ApplicationRowViewModel
            {
                Id = application.Id,
                Name = application.Name,
            })
            .ToList();

        return View(rows);
    }
}
