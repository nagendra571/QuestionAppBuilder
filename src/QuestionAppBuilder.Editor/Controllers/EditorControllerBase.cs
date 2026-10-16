using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

[Area(EditorArea.Name)]
public abstract class EditorControllerBase : Controller
{
    protected EditorControllerBase(QuestionAppBuilderEditorOptions editorOptions)
    {
        EditorOptions = editorOptions;
    }

    protected QuestionAppBuilderEditorOptions EditorOptions { get; }

    protected void SetEditorError(string message)
    {
        TempData["EditorError"] = message;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ViewData["QuestionAppBuilderLayout"] = EditorOptions.LayoutName;
        base.OnActionExecuting(context);
    }
}
