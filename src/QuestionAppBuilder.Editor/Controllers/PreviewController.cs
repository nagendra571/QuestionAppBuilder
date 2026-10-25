using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using System.Security.Cryptography;
using System.Globalization;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Core.Rendering;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

public sealed class PreviewController : EditorControllerBase
{
    private readonly IDraftService _drafts; private readonly IDefinitionEvaluator _evaluator; private readonly IQuestionnaireRenderer _renderer;
    private readonly ICompositeViewEngine _viewEngine;
    public PreviewController(QuestionAppBuilderEditorOptions options, IDraftService drafts, IDefinitionEvaluator evaluator,
        IQuestionnaireRenderer renderer, ICompositeViewEngine viewEngine)
        : base(options) { _drafts = drafts; _evaluator = evaluator; _renderer = renderer; _viewEngine = viewEngine; }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(int applicationId, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        try
        {
            var snapshot = await _drafts.GetAsync(applicationId, ct);
            if (request.ExpectedRevision is not null)
            {
                byte[] expectedRevision;
                try { expectedRevision = Convert.FromBase64String(request.ExpectedRevision); }
                catch (FormatException) { return BadRequest(new { message = "Reload the workspace before evaluating this preview." }); }
                if (expectedRevision.Length != snapshot.Revision.Length
                    || !CryptographicOperations.FixedTimeEquals(expectedRevision, snapshot.Revision))
                    return Conflict(new { message = "The draft changed since this preview page loaded. Reload the workspace before evaluating it again." });
            }
            var answers = new Dictionary<int, AnswerValue>();
            foreach (var (questionId, input) in request.Answers)
            {
                decimal? number = null;
                if (!string.IsNullOrWhiteSpace(input.Number))
                {
                    if (!decimal.TryParse(input.Number, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        return UnprocessableEntity(new { message = $"Question {questionId} needs a valid number." });
                    number = parsed;
                }
                var answerType = snapshot.Definition.Categories.SelectMany(category => category.Elements)
                    .FirstOrDefault(element => element.Question?.Id == questionId)?.Question?.AnswerType;
                var selections = input.SelectedOptionIds ?? new List<int>();
                var selectedOptionIds = answerType is AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes
                    ? selections
                    : selections.Count == 0 ? null : selections;
                answers[questionId] = new AnswerValue(input.Text, number, selectedOptionIds);
            }
            var checkCompleteness = request.Submit || request.Review
                || request.TargetCategoryIndex is int requestedTarget && requestedTarget > request.CategoryIndex;
            var result = _evaluator.Evaluate(snapshot.Definition, answers, requireComplete: checkCompleteness);
            var model = _renderer.Create(snapshot.Definition, answers, result, preview: true);
            var categoryIndex = Math.Clamp(request.TargetCategoryIndex ?? request.CategoryIndex, 0, Math.Max(0, model.Categories.Count - 1));
            var fieldErrors = result.AnswerIssues.Where(issue => issue.QuestionId is not null)
                .GroupBy(issue => issue.QuestionId!.Value).ToDictionary(group => group.Key, group => group.First().Message);
            string? summary = null;
            var positions = model.Categories.SelectMany(category => category.Elements.Where(element => element.Question is not null)
                .Select(element => (element.Question!.Id, category.Index))).ToDictionary(item => item.Id, item => item.Index);
            if (request.TargetCategoryIndex is int target && target > request.CategoryIndex
                && snapshot.Definition.Version.NavigationMode == NavigationMode.Sequential)
            {
                var firstIncomplete = result.AnswerIssues.Where(issue => issue.QuestionId is int id
                    && positions.TryGetValue(id, out var position) && position < target)
                    .Select(issue => positions[issue.QuestionId!.Value]).DefaultIfEmpty(target).Min();
                if (firstIncomplete < target)
                {
                    categoryIndex = firstIncomplete;
                    summary = "Complete the required fields before continuing.";
                }
            }
            if (request.Review && snapshot.Definition.Version.NavigationMode == NavigationMode.Sequential)
            {
                var firstIncomplete = result.AnswerIssues.Where(issue => issue.QuestionId is int id && positions.ContainsKey(id))
                    .Select(issue => positions[issue.QuestionId!.Value]).DefaultIfEmpty(categoryIndex).Min();
                if (result.AnswerIssues.Any(issue => issue.QuestionId is int id && positions.ContainsKey(id)))
                {
                    categoryIndex = firstIncomplete;
                    summary = "Complete the required fields before reviewing your answers.";
                }
            }
            var page = new QuestionAppBuilder.Core.ViewModels.RuntimePreviewPageModel(model, categoryIndex,
                snapshot.Definition.Version.NavigationMode, snapshot.Definition.Version.ResultVisibility,
                fieldErrors, summary, request.Review && summary is null, false, result.Score, result.DefinitionIssues);
            string html;
            if (request.Submit && result.AnswerIssues.Count == 0 && result.DefinitionIssues.Count == 0)
            {
                page = page with { IsReceipt = true, IsReview = false };
                html = await RenderPartialAsync("_RuntimePreviewReceipt", page);
            }
            else if (request.Submit || request.Review && summary is null)
            {
                page = page with { IsReview = true };
                html = await RenderPartialAsync("_RuntimePreviewReview", page);
            }
            else if (model.Categories.Count == 0) html = await RenderEditorPreviewPartialAsync("_RuntimePreviewEmpty", page);
            else html = await RenderPartialAsync("_RuntimePreview", page);
            var hiddenItems = BuildHiddenItems(snapshot.Definition, result.HiddenElementReasons);
            var failedValidationRules = BuildFailedValidationItems(snapshot.Definition, result.FailedValidationRules);
            return Json(new PreviewResponse(model, result.Score, result.AnswerIssues, result.DefinitionIssues)
            {
                Html = html,
                NavigationMode = snapshot.Definition.Version.NavigationMode,
                ResultVisibility = snapshot.Definition.Version.ResultVisibility,
                CategoryIndex = categoryIndex,
                IsReceipt = page.IsReceipt,
                Revision = Convert.ToBase64String(snapshot.Revision),
                HiddenItems = hiddenItems,
                FailedValidationRules = failedValidationRules,
                Scoring = BuildScoringPreview(snapshot.Definition, result.Score)
            });
        }
        catch (QuestionAppBuilder.Domain.Exceptions.NotFoundException) { return NotFound(); }
    }

    private async Task<string> RenderPartialAsync(string viewName, object model)
        => await RenderPartialPathAsync($"~/Areas/QuestionAppBuilderCore/Views/Shared/{viewName}.cshtml", viewName, model);

    private async Task<string> RenderEditorPreviewPartialAsync(string viewName, object model)
        => await RenderPartialPathAsync($"~/Areas/QuestionAppBuilderEditor/Views/Preview/{viewName}.cshtml", viewName, model);

    private async Task<string> RenderPartialPathAsync(string path, string viewName, object model)
    {
        var result = _viewEngine.GetView(null, path, isMainPage: false);
        if (!result.Success) throw new InvalidOperationException($"The runtime preview view '{viewName}' could not be located.");
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), ModelState) { Model = model };
        using var writer = new StringWriter();
        await result.View.RenderAsync(new ViewContext(ControllerContext, result.View, viewData, TempData, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private IReadOnlyList<HiddenPreviewItem>? BuildHiddenItems(ApplicationVersionGraph graph, IReadOnlyList<HiddenElementReason>? reasons)
    {
        if (reasons is null || graph.Version.Status != VersionStatus.Draft) return null;
        var result = new List<HiddenPreviewItem>(reasons.Count);
        var categoriesById = graph.Categories.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        var elementsByCategoryId = graph.Categories.ToDictionary(category => category.Id,
            category => category.Elements.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First()));
        var questionElementsById = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).GroupBy(element => element.Question!.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var conditionsById = graph.VisibilityConditions.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        var conditionSummaries = new Dictionary<int, string?>();
        var dynamicTextUrl = Url.Action("Index", "Composition", new { id = graph.Version.ApplicationId }) + "#DynamicText";
        foreach (var reason in reasons)
        {
            if (!categoriesById.TryGetValue(reason.CategoryId, out var category)
                || !elementsByCategoryId.TryGetValue(reason.CategoryId, out var categoryElements)
                || !categoryElements.TryGetValue(reason.ElementId, out var element)) return null;
            var parent = reason.ParentElementId is int parentId && categoryElements.TryGetValue(parentId, out var parentElement)
                ? parentElement : null;
            var option = parent?.Question?.Options.FirstOrDefault(item => item.Id == reason.ParentOptionId);
            var kind = element.Question is null ? "Content" : "Question";
            var itemFragment = $"#element-{element.Id}";
            string? configurationUrl = null;
            string message;
            switch (reason.Code)
            {
                case "category-condition":
                    var categoryRequirement = DescribeCondition(reason.ConditionId);
                    message = categoryRequirement is null
                        ? $"Category {category.Name} is hidden because its assigned visibility condition is missing or incomplete."
                        : $"Category {category.Name} is hidden because its visibility condition did not match the preview answers. It requires {categoryRequirement}.";
                    configurationUrl = "#visibility";
                    break;
                case "element-condition":
                    var elementRequirement = DescribeCondition(reason.ConditionId);
                    message = elementRequirement is null ? "This item is hidden because its assigned visibility condition is missing or incomplete."
                        : $"This item's visibility condition did not match the preview answers. It requires {elementRequirement}.";
                    configurationUrl = "#visibility";
                    break;
                case "parent-unavailable":
                    message = "This follow-up depends on a parent question or option that is unavailable in the draft.";
                    break;
                case "parent-hidden":
                    message = $"This follow-up is hidden because its parent question, {parent?.Label ?? "parent question"}, is hidden by another rule.";
                    if (parent is not null) configurationUrl = $"#element-{parent.Id}";
                    break;
                case "parent-option-unselected":
                    message = $"This follow-up is hidden because the preview does not select {parent?.Label ?? "the parent question"} / {option?.Label ?? "the parent option"}.";
                    if (parent is not null) configurationUrl = $"#element-{parent.Id}";
                    break;
                case "dynamic-content-unavailable":
                    message = "This content block is hidden because a dynamic value used by its label is unavailable.";
                    configurationUrl = dynamicTextUrl;
                    break;
                default:
                    return null;
            }
            result.Add(new HiddenPreviewItem(element.Label, kind, message, itemFragment, configurationUrl));
        }
        return result;

        string? DescribeCondition(int? conditionId)
        {
            if (conditionId is not int id) return null;
            if (conditionSummaries.TryGetValue(id, out var cached)) return cached;
            conditionsById.TryGetValue(id, out var condition);
            var summary = PreviewController.DescribeCondition(graph, condition, questionElementsById);
            conditionSummaries[id] = summary;
            return summary;
        }
    }

    private ScoringPreview? BuildScoringPreview(ApplicationVersionGraph graph, SubmissionScore score)
    {
        if (graph.Version.Status != VersionStatus.Draft) return null;
        var diagnostics = score.Diagnostics;
        if (diagnostics is null) return null;
        var categoriesById = graph.Categories.GroupBy(category => category.Id).ToDictionary(group => group.Key, group => group.First());
        var elementsByQuestionId = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).GroupBy(element => element.Question!.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var categoryNameByElementId = graph.Categories.SelectMany(category => category.Elements
                .Select(element => (element.Id, category.Name)))
            .GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First().Name);
        var questionScores = score.Questions.GroupBy(item => item.QuestionId).ToDictionary(group => group.Key, group => group.First());
        var categories = score.Categories.Select(category => new ScoringCategoryPreview(
            categoriesById.TryGetValue(category.CategoryId, out var entity) ? entity.Name : "Unassigned category", category.Score)).ToArray();
        var workspaceUrl = Url.Action("Index", "Drafts", new { applicationId = graph.Version.ApplicationId });
        if (workspaceUrl is null) return null;

        var questions = new List<ScoringQuestionPreview>(diagnostics.Questions.Count);
        foreach (var diagnostic in diagnostics.Questions)
        {
            if (!elementsByQuestionId.TryGetValue(diagnostic.QuestionId, out var element) || element.Question is null
                || !questionScores.TryGetValue(diagnostic.QuestionId, out var scored)) return null;
            var question = element.Question;
            var categoryName = categoryNameByElementId.GetValueOrDefault(element.Id, "Unassigned category");
            var rules = question.Rules.OrderBy(rule => rule.SortOrder).ThenBy(rule => rule.Id).ToArray();
            var diagnosticsByRule = diagnostic.Rules.GroupBy(rule => rule.RuleId).ToDictionary(group => group.Key, group => group.First());
            var scoreRulesById = scored.Rules.GroupBy(rule => rule.RuleId).ToDictionary(group => group.Key, group => group.First());
            var previewRules = new List<QuestionScoringRulePreview>(rules.Length);
            for (var index = 0; index < rules.Length; index++)
            {
                var rule = rules[index];
                var ordinalName = $"Rule {index + 1} · {QuestionAppBuilder.Editor.ViewModels.QuestionFormViewModel.SplitPascalCase(rule.Family.ToString())}";
                if (!diagnostic.IsApplicable)
                {
                    previewRules.Add(new QuestionScoringRulePreview(ordinalName, DescribeQuestionScoringRule(question, rule),
                        "Not applicable because this question was hidden in the preview.", false, false, 0, null, null,
                        null, null, false, null, workspaceUrl + "#rule-" + rule.Id));
                    continue;
                }
                if (!diagnosticsByRule.TryGetValue(rule.Id, out var detail) || !scoreRulesById.TryGetValue(rule.Id, out var scoreTrace)) return null;
                string? supersedingName = null;
                if (detail.SupersedingRuleId is int supersedingId)
                {
                    var supersedingIndex = Array.FindIndex(rules, candidate => candidate.Id == supersedingId);
                    supersedingName = supersedingIndex >= 0
                        ? $"Rule {supersedingIndex + 1} · {QuestionAppBuilder.Editor.ViewModels.QuestionFormViewModel.SplitPascalCase(rules[supersedingIndex].Family.ToString())}"
                        : "an earlier matching rule";
                }
                previewRules.Add(new QuestionScoringRulePreview(ordinalName, DescribeQuestionScoringRule(question, rule),
                    DescribeQuestionRuleOutcome(detail, scoreTrace.Points, supersedingName), detail.Matches, detail.Contributes,
                    scoreTrace.Points, detail.MatchedPoints?.ToString(CultureInfo.InvariantCulture),
                    detail.Units?.ToString("0.############################", CultureInfo.InvariantCulture), detail.Rate,
                    detail.Cap, detail.CapReached, supersedingName, workspaceUrl + "#rule-" + rule.Id));
            }
            var selectedOptions = diagnostic.SelectedOptionIds
                .Select(id => question.Options.FirstOrDefault(option => option.Id == id)?.Label)
                .Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label!).ToArray();
            questions.Add(new ScoringQuestionPreview(element.Label, categoryName, question.AnswerType.ToString(), scored.Points,
                diagnostic.IsApplicable, diagnostic.Answered,
                diagnostic.AnswerNumber?.ToString("0.############################", CultureInfo.InvariantCulture),
                selectedOptions, question.ScoringMode.ToString(), previewRules, workspaceUrl + "#element-" + element.Id));
        }

        IReadOnlyList<CombinedScoringRulePreview>? combinedRules = null;
        var combinedErrors = diagnostics.CombinedDefinitionErrors?.ToList() ?? [];
        if (diagnostics.CombinedRules is not null)
        {
            if (diagnostics.CombinedRules.Count == 0) combinedRules = [];
            else
            {
                CompositionDefinition definition;
                try { definition = CompositionDefinition.Read(graph.Version.CompositionJson); }
                catch (System.Text.Json.JsonException)
                {
                    combinedErrors.Add("Combined score explanations could not be matched to the saved configuration.");
                    return new ScoringPreview(diagnostics.UnavailableReason, combinedErrors, categories, questions, null);
                }
                if (definition.Rules is null)
                {
                    combinedErrors.Add("Combined score explanations could not be matched to the saved configuration.");
                    return new ScoringPreview(diagnostics.UnavailableReason, combinedErrors, categories, questions, null);
                }
                var rulesById = definition.Rules.Where(rule => rule is not null).GroupBy(rule => rule.Id)
                    .ToDictionary(group => group.Key, group => group.First());
                var questionsById = elementsByQuestionId;
                var categoryByKey = graph.Categories.GroupBy(category => category.LogicalKey)
                    .ToDictionary(group => group.Key, group => group.First());
                var formatter = new PreviewCompositionFormatter(graph, definition);
                var compositionUrl = Url.Action("Index", "Composition", new { id = graph.Version.ApplicationId });
                if (compositionUrl is null) return null;
                var previews = new List<CombinedScoringRulePreview>(diagnostics.CombinedRules.Count);
                foreach (var item in diagnostics.CombinedRules)
                {
                    if (!rulesById.TryGetValue(item.RuleId, out var rule) || !categoryByKey.TryGetValue(rule.CategoryKey, out var category)
                        || rule.Checks is null || rule.Conditions is null
                        || item.Checks.Any(check => check.Index < 0 || check.Index >= rule.Checks.Count)
                        || item.Conditions.Any(check => check.Index < 0 || check.Index >= rule.Conditions.Count))
                    {
                        combinedErrors.Add("Some combined score explanations could not be matched to the saved configuration.");
                        return new ScoringPreview(diagnostics.UnavailableReason, combinedErrors, categories, questions, null);
                    }
                    var targetName = item.TargetQuestionId is int targetId && questionsById.TryGetValue(targetId, out var target)
                        ? target.Label : item.Outcome == "no-visible-target" ? "No visible question in the rule category" : "Target question unavailable";
                    var targetUrl = item.TargetQuestionId is int questionId && questionsById.TryGetValue(questionId, out var targetElement)
                        ? workspaceUrl + "#element-" + targetElement.Id : null;
                    var supersedingName = item.SupersedingRuleId is Guid winnerId && rulesById.TryGetValue(winnerId, out var winner)
                        ? winner.Name : null;
                    string OutcomeText() => item.Outcome switch
                    {
                        "no-visible-target" => "No visible question in the rule category could serve as its target.",
                        "target-hidden" => "The configured target question was hidden in this preview.",
                        "condition-not-matched" => "The rule's configured conditions did not all match.",
                        "checks-not-matched" => "The required checks did not match.",
                        "awarded" => $"The rule awarded {item.AwardedPoints} point{(item.AwardedPoints == 1 ? "" : "s")}.",
                        "band-suppressed" => $"The rule matched, but its band was already awarded by {supersedingName ?? "an earlier rule"}.",
                        _ => "This score rule has an unsupported evaluation outcome."
                    };
                    previews.Add(new CombinedScoringRulePreview(rule.Name, category.Name, targetName, OutcomeText(), item.Outcome,
                        item.AwardedPoints, rule.BandGroup, supersedingName, rule.AnyCheck,
                        Math.Max(0, rule.Conditions.Count - item.Conditions.Count), Math.Max(0, rule.Checks.Count - item.Checks.Count),
                        item.Conditions.Select(check => formatter.FormatCheck(rule.Conditions[check.Index], check)).ToArray(),
                        item.Checks.Select(check => formatter.FormatCheck(rule.Checks[check.Index], check)).ToArray(),
                        compositionUrl + "#Rules/" + Uri.EscapeDataString(rule.Id.ToString("D")), targetUrl));
                }
                combinedRules = previews;
            }
        }
        return new ScoringPreview(diagnostics.UnavailableReason, combinedErrors, categories, questions, combinedRules);
    }

    private static string DescribeQuestionScoringRule(Question question, ScoringRule rule)
    {
        static string Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "no limit";
        string NumericRange()
        {
            if (rule.MinValue is decimal min && rule.MaxValue is decimal max)
                return $"from {Number(min)} ({(rule.MinInclusive ? "inclusive" : "exclusive")}) through {Number(max)} ({(rule.MaxInclusive ? "inclusive" : "exclusive")})";
            if (rule.MinValue is decimal minimum)
                return rule.MinInclusive ? $"at least {Number(minimum)}" : $"greater than {Number(minimum)}";
            if (rule.MaxValue is decimal maximum)
                return rule.MaxInclusive ? $"at most {Number(maximum)}" : $"less than {Number(maximum)}";
            return "within the configured range";
        }
        var requirement = rule.Family switch
        {
            RuleFamily.Presence => "the answer is present",
            RuleFamily.Selection => SelectionRequirement(),
            RuleFamily.NumericRange => $"the number is {NumericRange()}",
            _ => "an unsupported condition"
        };

        var award = rule.AwardMethod switch
        {
            AwardMethod.Fixed => $"Awards {rule.Points} point{(rule.Points == 1 ? "" : "s")} when {requirement}.",
            AwardMethod.PointsPerUnit => $"Awards {rule.PointsPerUnit} point{(rule.PointsPerUnit == 1 ? "" : "s")} per whole-number unit when {requirement}"
                + (rule.MaximumPoints is int cap ? $", up to {cap} point{(cap == 1 ? "" : "s")}." : "."),
            _ => $"This rule has an unsupported award method; it would apply when {requirement}."
        };
        return award;

        string SelectionRequirement()
        {
            if (rule.MembershipOperator is not (MembershipOperator.AllOf or MembershipOperator.AnyOf))
                return "the configured option selection condition is supported";
            var labels = rule.Options.Select(option => question.Options.FirstOrDefault(candidate => candidate.Id == option.QuestionOptionId)?.Label)
                .Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label!).Distinct(StringComparer.Ordinal).ToArray();
            if (labels.Length == 0) return "a configured selection condition with one or more existing options is met";
            return $"{(rule.MembershipOperator == MembershipOperator.AllOf ? "all" : "any")} of these options are selected: {string.Join(", ", labels)}";
        }
    }

    private static string DescribeQuestionRuleOutcome(QuestionScoringRuleDiagnostic detail, int points, string? supersedingName)
    {
        var units = detail.Units?.ToString("0.############################", CultureInfo.InvariantCulture);
        var perUnit = detail.Rate is int rate
            ? $" Units: {units ?? "unavailable"}; rate: {rate}; cap: {(detail.Cap is int cap ? cap.ToString(CultureInfo.InvariantCulture) : "none")}{(detail.CapReached ? " (reached)" : "")}."
            : "";
        return detail.Outcome switch
        {
            "awarded" => $"Matched and contributed {points} point{(points == 1 ? "" : "s")}.{perUnit}",
            "first-match-suppressed" => $"Matched, but contributed no points because {supersedingName ?? "an earlier rule"} was the first matching rule.{perUnit}",
            "condition-not-matched" => $"The answer did not match this rule.{perUnit}",
            "invalid-award-configuration" => "Award settings are invalid, so no award was evaluated.",
            "invalid-per-unit-answer" => $"No points-per-unit award: the answer must be a valid whole, nonnegative numeric value within the question's configured bounds.{perUnit}",
            "per-unit-limit" => $"The calculated award exceeded the supported point limit.{perUnit}",
            "unsupported-scoring-mode" => "The rule matched, but this question uses an unsupported scoring mode.",
            _ => "Scoring outcome is unavailable."
        };
    }

    private IReadOnlyList<FailedCombinedValidationPreviewItem>? BuildFailedValidationItems(
        ApplicationVersionGraph graph, IReadOnlyList<CompositionValidationFailure>? failures)
    {
        if (failures is null || graph.Version.Status != VersionStatus.Draft) return null;
        if (failures.Count == 0) return [];
        CompositionDefinition composition;
        try { composition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (System.Text.Json.JsonException) { return null; }
        if (composition.Rules is null) return null;

        var rulesById = composition.Rules.Where(rule => rule is not null).GroupBy(rule => rule.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var elementsByQuestionId = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).GroupBy(element => element.Question!.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var formatter = new PreviewCompositionFormatter(graph, composition);

        var workspaceUrl = Url.Action("Index", "Drafts", new { applicationId = graph.Version.ApplicationId });
        var compositionUrl = Url.Action("Index", "Composition", new { id = graph.Version.ApplicationId });
        if (workspaceUrl is null || compositionUrl is null) return null;
        var result = new List<FailedCombinedValidationPreviewItem>(failures.Count);
        foreach (var failure in failures)
        {
            if (!rulesById.TryGetValue(failure.RuleId, out var rule)
                || !elementsByQuestionId.TryGetValue(failure.TargetQuestionId, out var target)
                || rule.Checks is null || failure.Checks.Any(check => check.Index < 0 || check.Index >= rule.Checks.Count)) return null;
            var checks = failure.Checks.Select(item => formatter.FormatCheck(rule.Checks[item.Index], item)).ToArray();
            result.Add(new FailedCombinedValidationPreviewItem(rule.Name, rule.Message, target.Label,
                workspaceUrl + "#element-" + target.Id,
                compositionUrl + "#Rules/" + Uri.EscapeDataString(rule.Id.ToString("D")),
                failure.ShowWhileAnswering, failure.AnyCheck, rule.Checks.Count - failure.Checks.Count, checks));
        }
        return result;
    }

    private static string? DescribeCondition(ApplicationVersionGraph graph, VisibilityCondition? condition,
        IReadOnlyDictionary<int, Element> questionElementsById)
    {
        if (condition is null || condition.Predicates.Count == 0) return null;
        var expressions = condition.Predicates.Select(predicate =>
        {
            questionElementsById.TryGetValue(predicate.SourceQuestionId, out var source);
            var label = source?.Label ?? "a question";
            var options = source?.Question?.Options.Where(option => predicate.Options.Any(link => link.QuestionOptionId == option.Id))
                .Select(option => option.Label).ToArray() ?? [];
            return predicate.Operator switch
            {
                ConditionOperator.Answered => $"{label} is answered",
                ConditionOperator.Unanswered => $"{label} is unanswered",
                ConditionOperator.HasAnyOption => $"{label} has any of {JoinOptions(options)} selected",
                ConditionOperator.HasAllOptions => $"{label} has all of {JoinOptions(options)} selected",
                ConditionOperator.Equal => $"{label} equals {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "the configured number"}",
                ConditionOperator.NotEqual => $"{label} differs from {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "the configured number"}",
                ConditionOperator.GreaterThan => $"{label} is greater than {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "the configured number"}",
                ConditionOperator.GreaterThanOrEqual => $"{label} is at least {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "the configured number"}",
                ConditionOperator.LessThan => $"{label} is less than {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "the configured number"}",
                ConditionOperator.LessThanOrEqual => $"{label} is at most {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "the configured number"}",
                _ => (string?)null
            };
        }).ToArray();
        if (expressions.Any(expression => expression is null)) return null;
        var junction = condition.Junction == ConditionJunction.All ? "all" : "any";
        var summary = expressions.Length == 0 ? "no configured checks" : $"{junction} of these: {string.Join("; ", expressions)}";
        return summary.Length <= 280 ? summary : summary[..277] + "…";

        static string JoinOptions(IReadOnlyList<string> labels) => labels.Count == 0 ? "a configured option" : string.Join(", ", labels);
    }
}
