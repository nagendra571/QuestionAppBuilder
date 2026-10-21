using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class DraftsController : EditorControllerBase
{
    private readonly IDraftService _drafts;
    private readonly IApplicationDesignService _applications;

    public DraftsController(QuestionAppBuilderEditorOptions options, IDraftService drafts, IApplicationDesignService applications)
        : base(options) { _drafts = drafts; _applications = applications; }

    [HttpGet]
    public async Task<IActionResult> Index(int applicationId, CancellationToken ct)
    {
        try
        {
            var snapshot = await _drafts.GetAsync(applicationId, ct);
            var review = await _drafts.ReviewAsync(applicationId, ct);
            var input = ToInput(snapshot);
            var model = new DraftWorkspaceViewModel(applicationId, snapshot.Revision, snapshot.Definition, input,
                review.Errors.Select(Issue).ToArray(), review.Warnings.Select(Issue).ToArray(), EditorOptions.LayoutName);
            return View("Index", model);
        }
        catch (NotFoundException) { return NotFound(); }
    }

    [HttpGet]
    public async Task<IActionResult> Readiness(int applicationId, CancellationToken ct)
    {
        try
        {
            var review = await _drafts.ReviewAsync(applicationId, ct);
            return Json(new { revision = Convert.ToBase64String(review.Revision), errors = review.Errors, warnings = review.Warnings });
        }
        catch (NotFoundException) { return NotFound(); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int applicationId, [Bind(Prefix = "")] DraftWorkspaceInput input, CancellationToken ct)
    {
        if (input is null || input.ApplicationId != applicationId || input.ExpectedRevision is null || input.ExpectedRevision.Length == 0)
            return BadRequest(new { message = "The draft save request is incomplete." });
        if (!ModelState.IsValid)
            return UnprocessableEntity(new { message = "One or more draft fields have an invalid value.", errors = ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(entry => entry.Key, entry => entry.Value!.Errors.Select(error => error.ErrorMessage).ToArray()) });
        try
        {
            var snapshot = await _drafts.GetAsync(applicationId, ct);
            var graph = Apply(snapshot.Definition, input);
            var saved = await _drafts.SaveAsync(new SaveDraftRequest(applicationId, input.ExpectedRevision, graph), ct);
            if (WantsJson()) return Json(new { revision = Convert.ToBase64String(saved.Revision), saved = true });
            return RedirectToAction(nameof(Index), new { applicationId });
        }
        catch (DraftConflictException) { return Conflict(new { message = "This draft changed elsewhere. Your local edits are still available; reload before publishing." }); }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
        catch (KeyNotFoundException) { return UnprocessableEntity(new { message = "The draft request contains an item outside this version." }); }
    }

    private bool WantsJson() => Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        || Request.Headers["X-Requested-With"] == "QuestionAppBuilder";

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int applicationId, byte[] expectedRevision, string[]? acknowledgedWarningCodes, CancellationToken ct)
    {
        if (expectedRevision is null || expectedRevision.Length == 0) return BadRequest();
        var request = new PublishRequest(applicationId, expectedRevision,
            (acknowledgedWarningCodes ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal));
        try
        {
            var versionId = await _drafts.PublishAsync(request, ct);
            if (WantsJson()) return Json(new { published = true, versionId });
            return RedirectToAction("Detail", "Applications", new { id = applicationId });
        }
        catch (DraftConflictException) { return Conflict(new { message = "The draft changed. Reload readiness before publishing." }); }
        catch (NotFoundException) { return NotFound(); }
        catch (DefinitionValidationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }

    private static DraftReadinessItem Issue(PublicationIssue issue) => new(issue.Code, issue.ElementId, issue.Message);

    private static DraftWorkspaceInput ToInput(DraftSnapshot snapshot)
    {
        var graph = snapshot.Definition;
        return new DraftWorkspaceInput
        {
            ApplicationId = graph.Version.ApplicationId, ExpectedRevision = snapshot.Revision,
            Version = new DraftVersionInput { DisplayName = graph.Version.DisplayName, SubmitLabel = graph.Version.SubmitLabel,
                ConfirmationText = graph.Version.ConfirmationText, NavigationMode = graph.Version.NavigationMode,
                RespondentAccessMode = graph.Version.RespondentAccessMode, ResultVisibility = graph.Version.ResultVisibility },
            Categories = graph.Categories.Select(c => new DraftCategoryInput { Id = c.Id, Name = c.Name, SortOrder = c.SortOrder, VisibilityConditionId = c.VisibilityConditionId }).ToList(),
            Elements = graph.Categories.SelectMany(c => c.Elements).Select(e => new DraftElementInput { Id = e.Id, Label = e.Label, HelpText = e.HelpText, SortOrder = e.SortOrder, VisibilityConditionId = e.VisibilityConditionId }).ToList(),
            Questions = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!).Select(q => new DraftQuestionInput { Id = q.Id,
                AnswerType = q.AnswerType, IsRequired = q.IsRequired, RequiredMessage = q.RequiredMessage, ScoringMode = q.ScoringMode, MaxLength = q.MaxLength, MinValue = q.MinValue,
                MaxValue = q.MaxValue, MinSelections = q.MinSelections, MaxSelections = q.MaxSelections }).ToList(),
            Options = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).SelectMany(e => e.Question!.Options)
                .Select(o => new DraftOptionInput { Id = o.Id, Label = o.Label, Value = o.Value, SortOrder = o.SortOrder }).ToList(),
            Rules = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).SelectMany(e => e.Question!.Rules)
                .Select(r => new DraftRuleInput { Id = r.Id, Family = r.Family, Points = r.Points, SortOrder = r.SortOrder, MinInclusive = r.MinInclusive,
                    MaxInclusive = r.MaxInclusive, MembershipOperator = r.MembershipOperator, MinValue = r.MinValue, MaxValue = r.MaxValue,
                    OptionIds = r.Options.Select(o => o.QuestionOptionId).ToList() }).ToList(),
            Conditions = graph.VisibilityConditions.Select(c => new DraftConditionInput { Id = c.Id, Junction = c.Junction,
                Predicates = c.Predicates.Select(p => new DraftPredicateInput { Id = p.Id, SourceQuestionId = p.SourceQuestionId, Operator = p.Operator,
                    NumberValue = p.NumberValue, OptionIds = p.Options.Select(o => o.QuestionOptionId).ToList() }).ToList() }).ToList()
        };
    }

    private static ApplicationVersionGraph Apply(ApplicationVersionGraph graph, DraftWorkspaceInput input)
    {
        var version = graph.Version;
        version.DisplayName = input.Version.DisplayName; version.SubmitLabel = input.Version.SubmitLabel; version.ConfirmationText = input.Version.ConfirmationText ?? string.Empty;
        version.NavigationMode = input.Version.NavigationMode; version.RespondentAccessMode = input.Version.RespondentAccessMode; version.ResultVisibility = input.Version.ResultVisibility;
        var categories = graph.Categories.ToDictionary(c => c.Id); var elements = graph.Categories.SelectMany(c => c.Elements).ToDictionary(e => e.Id);
        var questions = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!).ToDictionary(q => q.Id);
        var options = questions.Values.SelectMany(q => q.Options).ToDictionary(o => o.Id);
        var rules = questions.Values.SelectMany(q => q.Rules).ToDictionary(r => r.Id);
        var conditions = graph.VisibilityConditions.ToDictionary(c => c.Id);
        foreach (var item in input.Categories) { var target = categories[item.Id]; target.Name = item.Name; target.SortOrder = item.SortOrder; target.VisibilityConditionId = item.VisibilityConditionId; }
        foreach (var item in input.Elements) { var target = elements[item.Id]; target.Label = item.Label; target.HelpText = item.HelpText; target.SortOrder = item.SortOrder; target.VisibilityConditionId = item.VisibilityConditionId; }
        foreach (var item in input.Questions) { var target = questions[item.Id]; target.AnswerType = item.AnswerType; target.IsRequired = item.IsRequired; target.RequiredMessage = string.IsNullOrWhiteSpace(item.RequiredMessage) ? null : item.RequiredMessage.Trim(); target.ScoringMode = item.ScoringMode;
            target.MaxLength = item.MaxLength; target.MinValue = item.MinValue; target.MaxValue = item.MaxValue; target.MinSelections = item.MinSelections; target.MaxSelections = item.MaxSelections; }
        foreach (var item in input.Options) { var target = options[item.Id]; target.Label = item.Label; target.Value = item.Value; target.SortOrder = item.SortOrder; }
        foreach (var item in input.Rules) { var target = rules[item.Id]; target.Family = item.Family; target.Points = item.Points; target.SortOrder = item.SortOrder;
            target.MinInclusive = item.MinInclusive; target.MaxInclusive = item.MaxInclusive; target.MembershipOperator = item.MembershipOperator; target.MinValue = item.MinValue; target.MaxValue = item.MaxValue;
            target.Options = item.OptionIds.Select(id => new ScoringRuleOption { ScoringRuleId = target.Id, QuestionOptionId = id }).ToList(); }
        foreach (var item in input.Conditions) { var target = conditions[item.Id]; target.Junction = item.Junction;
            foreach (var predicateInput in item.Predicates) { var predicate = target.Predicates.Single(p => p.Id == predicateInput.Id); predicate.SourceQuestionId = predicateInput.SourceQuestionId;
                predicate.Operator = predicateInput.Operator; predicate.NumberValue = predicateInput.NumberValue;
                predicate.Options = predicateInput.OptionIds.Select(id => new VisibilityPredicateOption { VisibilityPredicateId = predicate.Id, QuestionOptionId = id }).ToList(); } }
        return graph;
    }
}
