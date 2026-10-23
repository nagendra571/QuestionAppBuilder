using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

[Area(EditorArea.Name)]
[ServiceFilter(typeof(EditorAuthorizationFilter))]
public abstract class EditorControllerBase : Controller
{
    // The workspace posts the whole drafts as one form; ASP.NET Core's default of 1,024 values is reached ar ~50 questions.
    internal const int WorkspaceFormValueCountLimit = 50_000;

    protected EditorControllerBase(QuestionAppBuilderEditorOptions editorOptions)
    {
        EditorOptions = editorOptions;
    }

    protected QuestionAppBuilderEditorOptions EditorOptions { get; }

    protected void SetEditorError(string message)
    {
        TempData["EditorError"] = message;
    }

    protected IActionResult RedirectToDetail(int applicationId)
        => RedirectToAction("Detail", "Applications", new { id = applicationId });

    protected IActionResult RedirectToDetailWithValidationErrors(int applicationId)
    {
        var messages = ModelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct();

        SetEditorError(string.Join(" ", messages));
        return RedirectToDetail(applicationId);
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ViewData["QuestionAppBuilderLayout"] = EditorOptions.LayoutName;
        base.OnActionExecuting(context);
    }
}
