using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Controllers;

[RequestFormLimits(ValueCountLimit = EditorControllerBase.WorkspaceFormValueCountLimit)]
public sealed class StructureController : EditorControllerBase
{
    private readonly IApplicationDesignService _design;
    private readonly IDraftService _drafts;

    public StructureController(QuestionAppBuilderEditorOptions options, IApplicationDesignService design, IDraftService drafts)
        : base(options) { _design = design; _drafts = drafts; }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCategory(int applicationId, string categoryName, byte[] expectedRevision, CancellationToken ct)
    {
        try { await _design.AddCategoryAsync(applicationId, categoryName, ct, expectedRevision); }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { return StructureFailure(applicationId, StatusCodes.Status409Conflict, "The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddElement(int applicationId, int categoryId, string elementLabel, string? elementHelpText,
        string elementType, bool elementRequired, byte[] expectedRevision, CancellationToken ct)
    {
        try
        {
            if (string.Equals(elementType, "Content", StringComparison.Ordinal))
                await _design.AddContentElementAsync(categoryId, elementLabel, elementHelpText, ct, expectedRevision);
            else if (Enum.TryParse<AnswerType>(elementType, out var answerType) && Enum.IsDefined(answerType))
                await _design.AddQuestionAsync(categoryId, elementLabel, answerType, elementRequired, elementHelpText, ct, expectedRevision);
            else
                SetEditorError("Select a supported question type.");
        }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { SetEditorError("The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { SetEditorError(exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOption(int applicationId, int questionId, string optionLabel, string optionValue,
        byte[] expectedRevision, CancellationToken ct)
    {
        try { await _design.AddOptionAsync(questionId, optionLabel, optionValue, ct, expectedRevision); }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { SetEditorError("The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { SetEditorError(exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddFollowUp(int applicationId, int parentOptionId, string followUpLabel, AnswerType followUpType,
        bool followUpRequired, string? followUpHelpText, byte[] expectedRevision, CancellationToken ct)
    {
        if (!ModelState.IsValid || expectedRevision is null || expectedRevision.Length == 0)
            return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, "Check the follow-up fields and reload if the draft revision is missing.");
        try
        {
            var snapshot = await _drafts.GetAsync(applicationId, ct);
            if (!snapshot.Definition.Categories.SelectMany(c => c.Elements).Any(e => e.Question?.Options.Any(o => o.Id == parentOptionId) == true)) return NotFound();
            var child = await _design.AddFollowUpAsync(parentOptionId, followUpLabel, followUpType, followUpRequired, followUpHelpText, ct, expectedRevision);
            if (Request.Headers["X-Requested-With"] == "QuestionAppBuilder") return Json(new { saved = true, elementId = child.Id });
            return Redirect(Url.Action("Index", "Drafts", new { applicationId }) + "#element-" + child.Id);
        }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { return StructureFailure(applicationId, StatusCodes.Status409Conflict, "The draft changed. Reload before adding a follow-up."); }
        catch (ArgumentException) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, "Enter a question label for the follow-up."); }
        catch (DefinitionValidationException exception) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, exception.Message); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOtherOption(int applicationId, int questionId, byte[] expectedRevision, CancellationToken ct)
    {
        if (!ModelState.IsValid || expectedRevision is null || expectedRevision.Length == 0)
            return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, "Reload the draft before adding Other.");
        try
        {
            var graph = (await _drafts.GetAsync(applicationId, ct)).Definition;
            var question = graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question).FirstOrDefault(q => q?.Id == questionId);
            if (question is null) return NotFound();
            if (question.AnswerType is not (AnswerType.RadioGroup or AnswerType.Checkboxes or AnswerType.Dropdown))
                throw new DefinitionValidationException("Other options require a choice question.");
            if (!question.Options.Any(o => string.Equals(o.Value, "other", StringComparison.OrdinalIgnoreCase)))
                await _design.AddOptionAsync(questionId, "Other", "other", ct, expectedRevision);
        }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { return StructureFailure(applicationId, StatusCodes.Status409Conflict, "The draft changed. Reload before adding Other."); }
        catch (DefinitionValidationException exception) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRule(int applicationId, int questionId, RuleFamily family, int points,
        int[]? optionIds, decimal? minValue, decimal? maxValue, MembershipOperator? membershipOperator,
        byte[] expectedRevision, CancellationToken ct, AwardMethod awardMethod = AwardMethod.Fixed,
        int pointsPerUnit = 0, int? maximumPoints = null)
    {
        if (!ModelState.IsValid || expectedRevision is null || expectedRevision.Length == 0)
            return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity,
                "Check the scoring rule fields and reload if the draft revision is missing.");

        try
        {
            await _design.AddRuleWithAwardMethodAsync(questionId, family, points, membershipOperator, optionIds,
                minValue, maxValue, awardMethod, pointsPerUnit, maximumPoints, ct, expectedRevision);
        }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { SetEditorError("The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { SetEditorError(exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DuplicateCategory(int applicationId, int categoryId, byte[] expectedRevision, CancellationToken ct)
    {
        try { await _design.DuplicateCategoryAsync(categoryId, ct, expectedRevision); }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { SetEditorError("The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { SetEditorError(exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategory(int applicationId, int categoryId, string direction, byte[] expectedRevision, CancellationToken ct)
    {
        if (direction is not ("up" or "down")) return BadRequest();
        var snapshot = await _drafts.GetAsync(applicationId, ct);
        var categories = snapshot.Definition.Categories.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList();
        var index = categories.FindIndex(x => x.Id == categoryId);
        if (index < 0) return NotFound();
        var target = index + (direction == "up" ? -1 : 1);
        if (target < 0 || target >= categories.Count) return Complete(applicationId);
        try { await _design.MoveCategoryAsync(categoryId, target, ct, expectedRevision); }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { return StructureFailure(applicationId, StatusCodes.Status409Conflict, "The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DuplicateElement(int applicationId, int elementId, byte[] expectedRevision, CancellationToken ct)
    {
        try { await _design.DuplicateElementAsync(elementId, ct, expectedRevision); }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { return StructureFailure(applicationId, StatusCodes.Status409Conflict, "The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, exception.Message); }
        return Complete(applicationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveElement(int applicationId, int elementId, string direction, byte[] expectedRevision, CancellationToken ct)
    {
        if (direction is not ("up" or "down")) return BadRequest();
        var snapshot = await _drafts.GetAsync(applicationId, ct);
        var category = snapshot.Definition.Categories.SingleOrDefault(x => x.Elements.Any(e => e.Id == elementId));
        if (category is null) return NotFound();
        var elements = category.Elements.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList();
        var selected = elements.Single(e => e.Id == elementId);
        var siblings = elements.Where(e => e.ParentOptionId == selected.ParentOptionId).ToList();
        var index = siblings.IndexOf(selected);
        var siblingTarget = index + (direction == "up" ? -1 : 1);
        if (siblingTarget < 0 || siblingTarget >= siblings.Count) return Complete(applicationId);
        var target = elements.IndexOf(siblings[siblingTarget]);
        try { await _design.MoveElementAsync(elementId, target, ct, expectedRevision); }
        catch (NotFoundException) { return NotFound(); }
        catch (DraftConflictException) { return StructureFailure(applicationId, StatusCodes.Status409Conflict, "The draft changed. Reload the workspace before making this change."); }
        catch (DefinitionValidationException exception) { return StructureFailure(applicationId, StatusCodes.Status422UnprocessableEntity, exception.Message); }
        return Complete(applicationId);
    }

    private IActionResult Complete(int applicationId)
        => Request.Headers["X-Requested-With"] == "QuestionAppBuilder"
            ? Json(new { saved = true })
            : RedirectToAction("Index", "Drafts", new { applicationId });

    private IActionResult StructureFailure(int applicationId, int status, string message)
    {
        if (Request.Headers["X-Requested-With"] == "QuestionAppBuilder") return StatusCode(status, new { message });
        SetEditorError(message);
        return RedirectToAction("Index", "Drafts", new { applicationId });
    }
}
