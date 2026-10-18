using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using System.Text;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Core.Access;
using QuestionAppBuilder.Core.Authorization;
using QuestionAppBuilder.Core.Options;
using QuestionAppBuilder.Core.Rendering;
using QuestionAppBuilder.Core.ViewModels;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Core.Controllers;

[Area("QuestionAppBuilderCore")]
[ServiceFilter(typeof(RespondentFillAuthorizationFilter))]
public sealed class ResponsesController : Controller
{
    private readonly IResponseService _responses;
    private readonly ISubmissionCoordinator _submissions;
    private readonly IDefinitionEvaluator _evaluator;
    private readonly IQuestionnaireRenderer _renderer;
    private readonly QuestionAppBuilderRuntimeOptions _options;
    private readonly ICompositeViewEngine _viewEngine;

    public ResponsesController(IResponseService responses, ISubmissionCoordinator submissions, IDefinitionEvaluator evaluator,
        IQuestionnaireRenderer renderer, IOptions<QuestionAppBuilderRuntimeOptions> options, ICompositeViewEngine viewEngine)
    { _responses = responses; _submissions = submissions; _evaluator = evaluator; _renderer = renderer; _options = options.Value; _viewEngine = viewEngine; }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Landing(int applicationId, CancellationToken cancellationToken)
    {
        var landing = await _responses.GetLandingAsync(applicationId, cancellationToken);
        if (landing is null) { Response.StatusCode = StatusCodes.Status404NotFound; return View("Unavailable", "This questionnaire has not been published."); }
        var grant = await HttpContext.AuthenticateAsync(ResponseGrantHandler.Scheme);
        var hasStartGrant = landing.AcceptingNewResponses
            && grant.Succeeded
            && int.TryParse(grant.Principal?.FindFirst(ResponseGrantHandler.ApplicationIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var grantedApplicationId)
            && grantedApplicationId == applicationId
            && grant.Principal?.FindFirst(ResponseGrantHandler.ResponseIdClaim) is null;
        ViewData["StartUrl"] = $"{BasePath}/{applicationId}/Start";
        ViewData["AccessRequestUrl"] = $"{BasePath}/Access/Request";
        return View("Landing", new LandingPageModel(landing, applicationId, _options.LayoutName) { HasStartGrant = hasStartGrant });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(int applicationId, CancellationToken cancellationToken)
    {
        try
        {
            var started = await _responses.StartAsync(applicationId, cancellationToken);
            return LocalRedirect(EditUrl(started.Id, 0));
        }
        catch (NotFoundException exception) { Response.StatusCode = StatusCodes.Status404NotFound; return View("Unavailable", exception.Message); }
        catch (InvalidOperationException) { return Unauthorized("A respondent identity or valid email access link is required."); }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid responseId, int category = 0, CancellationToken cancellationToken = default)
    {
        ResponseSnapshot snapshot;
        try { snapshot = await _responses.GetAsync(responseId, cancellationToken); }
        catch (NotFoundException) { return NotFound(); }
        if (snapshot.Status == ResponseStatus.Submitted) return LocalRedirect(ReceiptUrl(responseId));
        var evaluation = _evaluator.Evaluate(snapshot.Definition, snapshot.Answers, requireComplete: false);
        var visibleCategories = _renderer.Create(snapshot.Definition, snapshot.Answers, evaluation, preview: false).Categories;
        if (visibleCategories.Count == 0) return View("Unavailable", "This questionnaire has no available categories.");
        category = Math.Clamp(category, 0, visibleCategories.Count - 1);
        if (snapshot.Definition.Version.NavigationMode == NavigationMode.Sequential && category > 0)
        {
            var firstIncomplete = FirstIncompleteCategory(snapshot.Definition, snapshot.Answers, visibleCategories.Count, category, out var errors);
            if (firstIncomplete < category)
            {
                var page = CreateResponsePage(snapshot, firstIncomplete, errors, "Complete the required fields before continuing.");
                return View("Edit", page);
            }
        }
        return View("Edit", CreateResponsePage(snapshot, category));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid responseId, ResponseFormInput form, CancellationToken cancellationToken)
    {
        ResponseSnapshot snapshot;
        try { snapshot = await _responses.GetAsync(responseId, cancellationToken); }
        catch (NotFoundException) { return NotFound(); }
        if (!TryParseAnswers(snapshot, form, out var answers, out var fieldErrors))
            return SaveError(snapshot, form, answers, fieldErrors, StatusCodes.Status422UnprocessableEntity);
        try
        {
            var saved = await _responses.SaveAsync(new SaveResponseRequest(responseId, DecodeRevision(form.ExpectedRevision), answers), cancellationToken);
            if (WantsJson())
            {
                var evaluated = _evaluator.Evaluate(saved.Definition, saved.Answers, requireComplete: false);
                var categoryCount = _renderer.Create(saved.Definition, saved.Answers, evaluated, preview: false).Categories.Count;
                var category = Math.Clamp(form.CurrentCategoryIndex, 0, Math.Max(0, categoryCount - 1));
                var page = CreateResponsePage(saved, category);
                var liveContentHtml = await RenderLiveContentAsync(page);
                return Json(new { success = true, revision = Convert.ToBase64String(saved.Revision), updatedUtc = saved.UpdatedUtc, liveContentHtml });
            }
            return LocalRedirect(EditUrl(responseId, form.CurrentCategoryIndex));
        }
        catch (ResponseConflictException)
        {
            if (WantsJson()) return Conflict(new { success = false, message = "This response changed in another window. Reload before saving." });
            return SaveError(snapshot, form, answers, new Dictionary<int, string>(), StatusCodes.Status409Conflict,
                "This response changed in another window. Your typed answers are still shown; reload the latest saved revision before continuing.");
        }
        catch (AnswerValidationException exception)
        {
            var errors = exception.QuestionIds.ToDictionary(id => id, _ => exception.Message);
            if (WantsJson()) return UnprocessableEntity(new { success = false, message = exception.Message });
            return SaveError(snapshot, form, answers, errors, StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Navigate(Guid responseId, ResponseFormInput form, CancellationToken cancellationToken)
    {
        ResponseSnapshot snapshot;
        try { snapshot = await _responses.GetAsync(responseId, cancellationToken); }
        catch (NotFoundException) { return NotFound(); }
        if (!TryParseAnswers(snapshot, form, out var answers, out var fieldErrors))
            return SaveError(snapshot, form, answers, fieldErrors, StatusCodes.Status422UnprocessableEntity);
        ResponseSnapshot saved;
        try { saved = await _responses.SaveAsync(new SaveResponseRequest(responseId, DecodeRevision(form.ExpectedRevision), answers), cancellationToken); }
        catch (ResponseConflictException)
        { return SaveError(snapshot, form, answers, new Dictionary<int, string>(), StatusCodes.Status409Conflict, "This response changed in another window. Your typed answers are still shown; reload before continuing."); }
        catch (AnswerValidationException exception)
        { return SaveError(snapshot, form, answers, exception.QuestionIds.ToDictionary(id => id, _ => exception.Message), StatusCodes.Status422UnprocessableEntity, exception.Message); }

        if (StringComparer.Ordinal.Equals(form.Intent, "Review")) return LocalRedirect(ReviewUrl(responseId));
        var evaluation = _evaluator.Evaluate(saved.Definition, saved.Answers, requireComplete: false);
        var categoryCount = _renderer.Create(saved.Definition, saved.Answers, evaluation, preview: false).Categories.Count;
        var target = Math.Clamp(form.TargetCategoryIndex, 0, Math.Max(0, categoryCount - 1));
        if (saved.Definition.Version.NavigationMode == NavigationMode.Sequential && target > form.CurrentCategoryIndex)
        {
            var firstIncomplete = FirstIncompleteCategory(saved.Definition, saved.Answers, categoryCount, target, out var errors);
            if (firstIncomplete < target)
                return View("Edit", CreateResponsePage(saved, firstIncomplete, errors, "Complete the required fields before continuing."));
        }
        return LocalRedirect(EditUrl(responseId, target));
    }

    [HttpGet]
    public async Task<IActionResult> Review(Guid responseId, CancellationToken cancellationToken)
    {
        ResponseSnapshot snapshot;
        try { snapshot = await _responses.GetAsync(responseId, cancellationToken); }
        catch (NotFoundException) { return NotFound(); }
        if (snapshot.Status == ResponseStatus.Submitted) return LocalRedirect(ReceiptUrl(responseId));
        var evaluation = _evaluator.Evaluate(snapshot.Definition, snapshot.Answers, requireComplete: false);
        ViewData["SubmitUrl"] = SubmitUrl(responseId);
        ViewData["EditUrl"] = EditUrl(responseId, _renderer.Create(snapshot.Definition, snapshot.Answers, evaluation, false).Categories.Count - 1);
        ViewData["EditCategoryUrlPrefix"] = $"{BasePath}/Responses/{responseId}?category=";
        return View("Review", new ReviewPageModel(snapshot, _renderer.Create(snapshot.Definition, snapshot.Answers, evaluation, false), _options.LayoutName,
            RequestId: Guid.NewGuid().ToString("D")));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid responseId, ResponseFormInput form, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _submissions.GetReceiptAsync(responseId, cancellationToken);
            return LocalRedirect(ReceiptUrl(responseId));
        }
        catch (NotFoundException) { }

        ResponseSnapshot snapshot;
        try { snapshot = await _responses.GetAsync(responseId, cancellationToken); }
        catch (NotFoundException) { return NotFound(); }
        if (!TryParseAnswers(snapshot, form, out var answers, out var fieldErrors))
            return SubmitError(snapshot, answers, fieldErrors, "Check the highlighted answers and try again.");
        try
        {
            var saved = await _responses.SaveAsync(new SaveResponseRequest(responseId, DecodeRevision(form.ExpectedRevision), answers), cancellationToken);
            var requestId = Guid.TryParse(form.RequestId, out var parsed) && parsed != Guid.Empty ? parsed : Guid.NewGuid();
            await _submissions.SubmitAsync(new SubmitResponseRequest(responseId, saved.Revision, requestId), cancellationToken);
            return LocalRedirect(ReceiptUrl(responseId));
        }
        catch (ResponseConflictException)
        { return SaveError(snapshot, form, answers, new Dictionary<int, string>(), StatusCodes.Status409Conflict, "This response changed in another window. Your typed answers are still shown; reload before submitting."); }
        catch (AnswerValidationException exception)
        {
            var errors = exception.QuestionIds.ToDictionary(id => id, _ => exception.Message);
            return SubmitError(snapshot with { Answers = answers }, answers, errors, "Complete the required fields before submitting.");
        }
        catch (DefinitionValidationException exception)
        { return SubmitError(snapshot with { Answers = answers }, answers, new Dictionary<int, string>(), exception.Message); }
        catch (InvalidOperationException exception)
        { return SubmitError(snapshot with { Answers = answers }, answers, new Dictionary<int, string>(), exception.Message); }
    }

    [HttpGet]
    public async Task<IActionResult> Receipt(Guid responseId, string? format = null, CancellationToken cancellationToken = default)
    {
        SubmissionReceipt receipt;
        try { receipt = await _submissions.GetReceiptAsync(responseId, cancellationToken); }
        catch (NotFoundException) { return NotFound(); }
        if (StringComparer.OrdinalIgnoreCase.Equals(format, "json") || Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            var payload = new Dictionary<string, object?> { ["submissionId"] = receipt.SubmissionId, ["responseId"] = receipt.ResponseId,
                ["submittedUtc"] = receipt.SubmittedUtc, ["confirmationText"] = receipt.ConfirmationText };
            if (receipt.Total is int total) payload["total"] = total;
            if (receipt.Categories is not null) payload["categories"] = receipt.Categories;
            return Json(payload);
        }
        return View("Receipt", new ReceiptPageModel(receipt, _options.LayoutName));
    }

    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
        => View("Mine", await _responses.ListMineAsync(50, ct: cancellationToken));

    private ResponsePageModel CreateResponsePage(ResponseSnapshot snapshot, int categoryIndex,
        IReadOnlyDictionary<int, string>? errors = null, string? summary = null, IReadOnlyDictionary<int, AnswerFormInput>? inputs = null)
    {
        var evaluation = _evaluator.Evaluate(snapshot.Definition, snapshot.Answers, requireComplete: false);
        var rendered = _renderer.Create(snapshot.Definition, snapshot.Answers, evaluation, preview: false);
        ViewData["SaveUrl"] = SaveUrl(snapshot.Id); ViewData["NavigateUrl"] = NavigateUrl(snapshot.Id); ViewData["ReviewUrl"] = ReviewUrl(snapshot.Id);
        return new ResponsePageModel(snapshot, rendered, categoryIndex, _options.LayoutName, errors, summary, null, null, inputs);
    }

    private async Task<string> RenderLiveContentAsync(ResponsePageModel model)
    {
        var result = _viewEngine.GetView(executingFilePath: null,
            viewPath: "~/Areas/QuestionAppBuilderCore/Views/Shared/_LiveContent.cshtml", isMainPage: false);
        if (!result.Success) throw new InvalidOperationException("The live response view could not be located.");
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), ModelState) { Model = model };
        viewData["FieldErrors"] = model.FieldErrors;
        viewData["InputValues"] = model.InputValues;
        using var writer = new StringWriter();
        var viewContext = new ViewContext(ControllerContext, result.View, viewData, TempData, writer, new HtmlHelperOptions());
        await result.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private IActionResult SaveError(ResponseSnapshot snapshot, ResponseFormInput form, IReadOnlyDictionary<int, AnswerValue> answers,
        IReadOnlyDictionary<int, string> errors, int statusCode, string? summary = null)
    {
        Response.StatusCode = statusCode;
        var shown = snapshot with { Answers = answers };
        var conflict = statusCode == StatusCodes.Status409Conflict;
        if (conflict) ViewData["ReloadUrl"] = EditUrl(snapshot.Id, Math.Max(0, form.CurrentCategoryIndex));
        var page = CreateResponsePage(shown, Math.Max(0, form.CurrentCategoryIndex), errors, summary, form.Answers);
        return View("Edit", page with { RejectedExpectedRevision = conflict ? form.ExpectedRevision : null });
    }

    private IActionResult SubmitError(ResponseSnapshot snapshot, IReadOnlyDictionary<int, AnswerValue> answers,
        IReadOnlyDictionary<int, string> errors, string summary)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        var evaluation = _evaluator.Evaluate(snapshot.Definition, answers, requireComplete: false);
        ViewData["SubmitUrl"] = SubmitUrl(snapshot.Id); ViewData["EditUrl"] = EditUrl(snapshot.Id, 0);
        return View("Review", new ReviewPageModel(snapshot with { Answers = answers }, _renderer.Create(snapshot.Definition, answers, evaluation, false), _options.LayoutName,
            errors, summary, Guid.NewGuid().ToString("D")));
    }

    private bool TryParseAnswers(ResponseSnapshot snapshot, ResponseFormInput form, out IReadOnlyDictionary<int, AnswerValue> answers,
        out IReadOnlyDictionary<int, string> fieldErrors)
    {
        var questionElements = snapshot.Definition.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).ToArray();
        var known = questionElements.ToDictionary(e => e.Question!.Id);
        var values = snapshot.Answers.ToDictionary(p => p.Key, p => p.Value);
        var errors = new Dictionary<int, string>();
        if (form.Answers.Keys.Any(id => !known.ContainsKey(id)))
        { answers = values; fieldErrors = errors; return false; }
        var currentCategoryId = _renderer.Create(snapshot.Definition, snapshot.Answers,
            _evaluator.Evaluate(snapshot.Definition, snapshot.Answers, false), false).Categories.ElementAtOrDefault(Math.Max(0, form.CurrentCategoryIndex))?.Id;
        var currentQuestionIds = questionElements.Where(e => e.CategoryId == currentCategoryId).Select(e => e.Question!.Id).ToHashSet();
        var keys = form.Answers.Keys.Concat(currentQuestionIds).Distinct().ToArray();
        foreach (var id in keys)
        {
            var question = known[id].Question!;
            var input = form.Answers.GetValueOrDefault(id) ?? new AnswerFormInput();
            AnswerValue value;
            switch (question.AnswerType)
            {
                case AnswerType.Textbox:
                case AnswerType.Textarea:
                    value = new AnswerValue(input.Text, null, null);
                    break;
                case AnswerType.Number:
                    if (string.IsNullOrWhiteSpace(input.Number)) value = new AnswerValue(null, null, null);
                    else if (decimal.TryParse(input.Number, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) value = new AnswerValue(null, number, null);
                    else { errors[id] = "Enter a valid number."; continue; }
                    break;
                case AnswerType.Dropdown:
                case AnswerType.RadioGroup:
                case AnswerType.Checkboxes:
                    value = new AnswerValue(null, null, input.SelectedOptionIds?.Distinct().ToArray());
                    break;
                default:
                    errors[id] = "This answer type is not supported."; continue;
            }
            values[id] = value;
        }
        answers = values; fieldErrors = errors;
        return errors.Count == 0;
    }

    private int FirstIncompleteCategory(ApplicationVersionGraph definition, IReadOnlyDictionary<int, AnswerValue> answers,
        int categoryCount, int target, out IReadOnlyDictionary<int, string> errors)
    {
        var evaluation = _evaluator.Evaluate(definition, answers, requireComplete: false);
        var visibleCategories = definition.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Where(c => evaluation.VisibleCategoryIds.Contains(c.Id)).Take(categoryCount).ToArray();
        var positions = visibleCategories.Select((category, index) => (category.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var questionToCategory = visibleCategories.SelectMany(c => c.Elements.Where(e => e.Question is not null).Select(e => (QuestionId: e.Question!.Id, c.Id)))
            .ToDictionary(x => x.QuestionId, x => positions[x.Id]);
        var mapped = new Dictionary<int, string>();
        foreach (var issue in evaluation.AnswerIssues.Where(i => i.QuestionId is int q && questionToCategory.TryGetValue(q, out var position) && position < target))
            mapped[issue.QuestionId!.Value] = issue.Message;
        foreach (var category in visibleCategories.Select((value, index) => (value, index)).Where(x => x.index < target))
        foreach (var element in category.value.Elements.Where(e => e.Question is not null && evaluation.VisibleQuestionIds.Contains(e.Question.Id)))
        {
            var question = element.Question!;
            if (question.IsRequired && !AnswerSemantics.IsAnswered(question.AnswerType, answers.GetValueOrDefault(question.Id)))
                mapped.TryAdd(question.Id, "A response is required.");
        }
        errors = mapped;
        if (mapped.Count == 0) return target;
        return mapped.Keys.Select(id => questionToCategory[id]).Min();
    }

    private bool WantsJson() => Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        || Request.Headers["X-Requested-With"] == "QuestionAppBuilder";
    private byte[] DecodeRevision(string? revision) => Convert.FromBase64String(revision ?? string.Empty);
    private string BasePath => _options.BasePath.TrimEnd('/');
    private string EditUrl(Guid id, int index) => $"{BasePath}/Responses/{id}?category={index}";
    private string ReviewUrl(Guid id) => $"{BasePath}/Responses/{id}/Review";
    private string ReceiptUrl(Guid id) => $"{BasePath}/Responses/{id}/Receipt";
    private string SaveUrl(Guid id) => $"{BasePath}/Responses/{id}/Save";
    private string NavigateUrl(Guid id) => $"{BasePath}/Responses/{id}/Navigate";
    private string SubmitUrl(Guid id) => $"{BasePath}/Responses/{id}/Submit";
}
