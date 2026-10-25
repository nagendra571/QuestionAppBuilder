using System.Globalization;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain;

namespace QuestionAppBuilder.Application.Evaluation;

public sealed class DefinitionEvaluator : IDefinitionEvaluator
{
    private readonly IScoringEngine _scoring;
    private readonly TimeProvider _timeProvider;

    public DefinitionEvaluator(IScoringEngine? scoring = null, TimeProvider? timeProvider = null)
    {
        _scoring = scoring ?? new ScoringEngine();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public EvaluationResult Evaluate(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers, bool requireComplete)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(answers);
        var orderedCategories = graph.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToArray();
        var questions = orderedCategories.SelectMany(c => FollowUpStructure.Ordered(c.Elements))
            .Where(e => e.Question is not null).Select(e => e.Question!).ToDictionary(q => q.Id);
        var conditions = graph.VisibilityConditions.ToDictionary(c => c.Id);
        var definitionIssues = ValidateDefinition(graph, orderedCategories, conditions);
        var answerIssues = new List<DefinitionIssue>();
        var visibleCategories = new HashSet<int>();
        var visibleQuestions = new HashSet<int>();
        var visibleElements = new HashSet<int>();
        var hiddenElementReasons = new List<HiddenElementReason>();

        foreach (var category in orderedCategories)
        {
            var categoryVisible = category.VisibilityConditionId is null
                || EvaluateCondition(category.VisibilityConditionId.Value, conditions, questions, answers, visibleQuestions);
            if (!categoryVisible)
            {
                foreach (var element in FollowUpStructure.Ordered(category.Elements))
                    hiddenElementReasons.Add(new HiddenElementReason(element.Id, category.Id, "category-condition", category.VisibilityConditionId));
                continue;
            }
            visibleCategories.Add(category.Id);

            foreach (var element in FollowUpStructure.Ordered(category.Elements))
            {
                var question = element.Question;
                var parent = FollowUpStructure.Parent(element, category.Elements);
                var parentVisible = element.ParentOptionId is null || parent?.Question is { } parentQuestion
                    && visibleQuestions.Contains(parentQuestion.Id)
                    && answers.GetValueOrDefault(parentQuestion.Id)?.SelectedOptionIds?.Contains(element.ParentOptionId.Value) == true;
                if (!parentVisible)
                {
                    var reason = parent?.Question is null ? "parent-unavailable"
                        : !visibleQuestions.Contains(parent.Question.Id) ? "parent-hidden" : "parent-option-unselected";
                    hiddenElementReasons.Add(new HiddenElementReason(element.Id, category.Id, reason,
                        ParentElementId: parent?.Id, ParentOptionId: element.ParentOptionId));
                    continue;
                }
                if (element.VisibilityConditionId is int conditionId
                    && !EvaluateCondition(conditionId, conditions, questions, answers, visibleQuestions))
                {
                    hiddenElementReasons.Add(new HiddenElementReason(element.Id, category.Id, "element-condition", conditionId,
                        parent?.Id, element.ParentOptionId));
                    continue;
                }
                visibleElements.Add(element.Id);
                if (question is not null) visibleQuestions.Add(question.Id);
            }
        }

        foreach (var questionId in answers.Keys)
        {
            if (!questions.ContainsKey(questionId))
                answerIssues.Add(new DefinitionIssue("unknown-question", null, $"Question {questionId} is not part of this definition.", questionId));
        }

        foreach (var questionId in visibleQuestions)
        {
            var question = questions[questionId];
            answers.TryGetValue(questionId, out var answer);
            if (answer is not null && !ValidateAnswer(question, answer, out var code, out var message))
                answerIssues.Add(new DefinitionIssue(code!, FindElementId(orderedCategories, questionId), message!, questionId));
            if (requireComplete && question.IsRequired && !AnswerSemantics.IsAnswered(question.AnswerType, answer))
                answerIssues.Add(new DefinitionIssue("required", FindElementId(orderedCategories, questionId), question.GetRequiredMessage(), questionId));
        }

        var score = _scoring.Score(graph, answers, visibleQuestions);
        var composition = QuestionAppBuilder.Application.Composition.CompositionEngine.Evaluate(graph, answers, visibleQuestions, visibleCategories);
        var evaluatedAtUtc = _timeProvider.GetUtcNow();
        var dynamicText = QuestionAppBuilder.Application.Composition.DynamicTextEngine.Evaluate(
            graph, answers, visibleQuestions, visibleCategories, composition, evaluatedAtUtc, visibleElements);
        var dynamicallyHiddenLabels = dynamicText.Texts.Where(text => text.Slot == QuestionAppBuilder.Domain.Composition.DynamicTextSlot.ElementLabel
            && text.IsHidden).Select(text => text.OwnerKey).ToHashSet();
        foreach (var (category, element) in orderedCategories.SelectMany(category => category.Elements.Select(element => (category, element)))
            .Where(pair => pair.element.Kind == ElementKind.Content && visibleElements.Contains(pair.element.Id)))
        {
            if (dynamicallyHiddenLabels.Contains(element.LogicalKey))
                hiddenElementReasons.Add(new HiddenElementReason(element.Id, category.Id, "dynamic-content-unavailable"));
        }
        composition = composition with
        {
            DynamicText = dynamicText,
            DefinitionErrors = composition.DefinitionErrors.Concat(dynamicText.DefinitionErrors).Distinct().ToArray()
        };
        foreach (var issue in composition.AnswerErrors)
            answerIssues.Add(new DefinitionIssue("combined-validation", FindElementId(orderedCategories, issue.QuestionId), issue.Message, issue.QuestionId));
        return new EvaluationResult(visibleCategories, visibleQuestions,
            definitionIssues.Concat(composition.DefinitionErrors.Where(m => !dynamicText.DefinitionErrors.Contains(m))
                .Select(m => new DefinitionIssue("invalid-composition", null, m)))
                .Concat(dynamicText.DefinitionErrors.Select(m => new DefinitionIssue("invalid-dynamic-text", null, m))).ToArray(), answerIssues, score)
        {
            Composition = composition,
            VisibleElementIds = visibleElements,
            HiddenElementReasons = hiddenElementReasons,
            FailedValidationRules = composition.FailedValidationRules
        };
    }

    private static IReadOnlyList<DefinitionIssue> ValidateDefinition(
        ApplicationVersionGraph graph, IReadOnlyList<Category> categories, IReadOnlyDictionary<int, VisibilityCondition> conditions)
    {
        var issues = new List<DefinitionIssue>();
        long maximumScore = 0;
        var questions = categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!).ToDictionary(q => q.Id);
        var sourcePositions = new Dictionary<int, int>();
        var categoryPositions = new Dictionary<int, int>();
        var elementPositions = new Dictionary<int, int>();
        var position = 0;
        foreach (var category in categories)
        {
            categoryPositions[category.Id] = position++;
            foreach (var element in FollowUpStructure.Ordered(category.Elements))
            {
                elementPositions[element.Id] = position;
                if (element.Question is not null) sourcePositions[element.Question.Id] = position;
                position++;
            }
        }
        var allElements = categories.SelectMany(c => c.Elements).ToArray();
        foreach (var category in categories)
        foreach (var element in category.Elements.Where(e => e.ParentOptionId is not null))
        {
            var parent = FollowUpStructure.Parent(element, allElements);
            if (parent?.Question is not { } parentQuestion || !category.Elements.Contains(parent)
                || parentQuestion.AnswerType is not (AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes)
                || element.Question is null || ReferenceEquals(parent, element)
                || FollowUpStructure.Branch(element, allElements).Contains(parent))
                issues.Add(new DefinitionIssue("invalid-follow-up", element.Id, "A follow-up must belong to an option on a choice question in the same category, without circular branches."));
        }
        foreach (var element in allElements)
        {
            var question = element.Question;
            if (question is null) continue;
            if (!Enum.IsDefined(question.AnswerType))
                issues.Add(new DefinitionIssue("invalid-answer-type", element.Id, "The answer type is not supported."));
            if (!Enum.IsDefined(question.ScoringMode))
                issues.Add(new DefinitionIssue("invalid-scoring-mode", element.Id, "The scoring mode is not supported."));
            foreach (var rule in question.Rules)
            {
                if (!Enum.IsDefined(rule.AwardMethod))
                    issues.Add(new DefinitionIssue("invalid-award-method", element.Id, "The scoring award method is not supported."));
                else if (rule.AwardMethod == AwardMethod.Fixed)
                {
                    if (rule.Points is < 0 or > ScoringAward.MaximumRuleAward)
                        issues.Add(new DefinitionIssue("invalid-points", element.Id, "Rule points must be between 0 and 1,000,000."));
                    else maximumScore += rule.Points;
                }
                else if (ScoringAward.TryGetMaximumAward(question, rule, out var maximumAward)) maximumScore += maximumAward;
                else issues.Add(new DefinitionIssue("invalid-points-per-unit", element.Id,
                    ScoringAward.GetMaximumAwardError(question, rule) ?? "The points-per-unit award is invalid."));
                if (!Enum.IsDefined(rule.Family))
                    issues.Add(new DefinitionIssue("invalid-rule-family", element.Id, "The scoring rule family is not supported."));
                if (rule.Family == RuleFamily.Selection && (!Enum.IsDefined(rule.MembershipOperator ?? (MembershipOperator)(-1)) || rule.Options.Count == 0))
                    issues.Add(new DefinitionIssue("invalid-selection-rule", element.Id, "Selection rules require a valid membership operator and at least one option."));
                if (rule.MinValue is decimal min && rule.MaxValue is decimal max && (min > max || min == max && (!rule.MinInclusive || !rule.MaxInclusive)))
                    issues.Add(new DefinitionIssue("invalid-numeric-range", element.Id, "A numeric rule has an empty or reversed range."));
            }
        }
        if (maximumScore > int.MaxValue)
            issues.Add(new DefinitionIssue("total-points-overflow", null, "Configured scoring points may exceed the supported total."));

        foreach (var category in categories)
            AddConditionIssue(category.VisibilityConditionId, conditions, questions, categoryPositions[category.Id], sourcePositions, null, issues);
        foreach (var element in categories.SelectMany(c => c.Elements))
            AddConditionIssue(element.VisibilityConditionId, conditions, questions, elementPositions[element.Id], sourcePositions, element.Id, issues);
        return issues;
    }

    private static void AddConditionIssue(int? conditionId, IReadOnlyDictionary<int, VisibilityCondition> conditions,
        IReadOnlyDictionary<int, Question> questions, int targetPosition, IReadOnlyDictionary<int, int> sourcePositions,
        int? elementId, ICollection<DefinitionIssue> issues)
    {
        if (conditionId is null) return;
        if (!conditions.TryGetValue(conditionId.Value, out var condition))
        {
            issues.Add(new DefinitionIssue("missing-condition", elementId, "The visibility condition is missing."));
            return;
        }
        if (!Enum.IsDefined(condition.Junction) || condition.Predicates.Count == 0)
            issues.Add(new DefinitionIssue("empty-condition", elementId, "A visibility condition requires a valid junction and at least one predicate."));
        foreach (var predicate in condition.Predicates)
        {
            if (!questions.TryGetValue(predicate.SourceQuestionId, out var sourceQuestion))
                issues.Add(new DefinitionIssue("missing-condition-source", elementId, "A visibility predicate refers to a missing question."));
            else if (sourcePositions[predicate.SourceQuestionId] >= targetPosition)
                issues.Add(new DefinitionIssue("forward-condition-reference", elementId, "A visibility predicate must refer to an earlier question."));
            if (!Enum.IsDefined(predicate.Operator))
                issues.Add(new DefinitionIssue("invalid-condition-operator", elementId, "A visibility predicate operator is not supported."));
            if ((predicate.Operator is ConditionOperator.HasAnyOption or ConditionOperator.HasAllOptions) && predicate.Options.Count == 0)
                issues.Add(new DefinitionIssue("empty-condition-options", elementId, "A choice predicate requires at least one option."));
            if (sourceQuestion is not null)
            {
                var choiceOperator = predicate.Operator is ConditionOperator.HasAnyOption or ConditionOperator.HasAllOptions;
                var numericOperator = predicate.Operator is ConditionOperator.Equal or ConditionOperator.NotEqual
                    or ConditionOperator.GreaterThan or ConditionOperator.GreaterThanOrEqual
                    or ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual;
                var validChoice = !choiceOperator || sourceQuestion.AnswerType is AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes
                    && predicate.Options.All(option => sourceQuestion.Options.Any(sourceOption => sourceOption.Id == option.QuestionOptionId));
                var validNumber = !numericOperator || sourceQuestion.AnswerType == AnswerType.Number && predicate.NumberValue is not null;
                var validOperandShape = choiceOperator || numericOperator ? validChoice && validNumber : predicate.NumberValue is null && predicate.Options.Count == 0;
                if (!validOperandShape)
                    issues.Add(new DefinitionIssue("invalid-condition-operand", elementId, "A visibility predicate operand does not match its source question."));
            }
        }
    }

    private static bool EvaluateCondition(int conditionId, IReadOnlyDictionary<int, VisibilityCondition> conditions,
        IReadOnlyDictionary<int, Question> questions, IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlySet<int> visible)
    {
        if (!conditions.TryGetValue(conditionId, out var condition) || condition.Predicates.Count == 0) return false;
        var values = condition.Predicates.Select(predicate => EvaluatePredicate(predicate, questions, answers, visible));
        return condition.Junction == ConditionJunction.All ? values.All(x => x) : values.Any(x => x);
    }

    private static bool EvaluatePredicate(VisibilityPredicate predicate, IReadOnlyDictionary<int, Question> questions,
        IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlySet<int> visible)
    {
        if (!questions.TryGetValue(predicate.SourceQuestionId, out var question)) return false;
        var exists = visible.Contains(question.Id) && answers.ContainsKey(question.Id);
        answers.TryGetValue(question.Id, out var answer);
        answer ??= new AnswerValue(null, null, null);
        var answered = exists && AnswerSemantics.IsAnswered(question.AnswerType, answer);
        var selected = answer.SelectedOptionIds ?? Array.Empty<int>();
        var optionIds = predicate.Options.Select(x => x.QuestionOptionId).ToArray();
        return predicate.Operator switch
        {
            ConditionOperator.Answered => answered,
            ConditionOperator.Unanswered => !answered,
            ConditionOperator.HasAnyOption => answered && optionIds.Any(selected.Contains),
            ConditionOperator.HasAllOptions => answered && optionIds.Length > 0 && optionIds.All(selected.Contains),
            ConditionOperator.Equal => answered && answer.Number == predicate.NumberValue,
            ConditionOperator.NotEqual => answered && answer.Number != predicate.NumberValue,
            ConditionOperator.GreaterThan => answered && answer.Number > predicate.NumberValue,
            ConditionOperator.GreaterThanOrEqual => answered && answer.Number >= predicate.NumberValue,
            ConditionOperator.LessThan => answered && answer.Number < predicate.NumberValue,
            ConditionOperator.LessThanOrEqual => answered && answer.Number <= predicate.NumberValue,
            _ => false
        };
    }

    private static bool ValidateAnswer(Question question, AnswerValue answer, out string? code, out string? message)
    {
        code = null;
        message = null;
        var valid = question.AnswerType switch
        {
            AnswerType.Textbox or AnswerType.Textarea => answer.Number is null && answer.SelectedOptionIds is null
                && (answer.Text is null || answer.Text.Length <= 4000 && (question.MaxLength is null || answer.Text.Length <= question.MaxLength)),
            AnswerType.UploadDocument => answer.Number is null && answer.SelectedOptionIds is null
                && (answer.Text is null || answer.Text.Length <= 255),
            AnswerType.Date => answer.Number is null && answer.SelectedOptionIds is null
                && (string.IsNullOrWhiteSpace(answer.Text) || DateOnly.TryParseExact(answer.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)),
            AnswerType.Number => answer.Text is null && answer.SelectedOptionIds is null
                && (answer.Number is null || (!question.WholeNumbersOnly || decimal.Truncate(answer.Number.Value) == answer.Number) && (question.MinValue is null || answer.Number >= question.MinValue) && (question.MaxValue is null || answer.Number <= question.MaxValue)),
            AnswerType.Dropdown or AnswerType.RadioGroup => answer.Text is null && answer.Number is null
                && ValidOptions(question, answer.SelectedOptionIds, 1),
            AnswerType.Checkboxes => answer.Text is null && answer.Number is null
                && ValidOptions(question, answer.SelectedOptionIds, question.MaxSelections),
            _ => false
        };
        if (!valid)
        {
            code = "invalid-answer";
            message = "The answer is malformed, too long, outside its allowed range, or contains an invalid option.";
        }
        else if (answer.SelectedOptionIds is { } selected && question.MinSelections is int minimum && selected.Count < minimum)
        {
            code = "selection-count";
            message = $"Select at least {minimum} option(s).";
            return false;
        }
        return valid;
    }

    private static bool ValidOptions(Question question, IReadOnlyCollection<int>? selected, int? max)
    {
        if (selected is null) return true;
        var allowed = question.Options.Select(x => x.Id).ToHashSet();
        return selected.Distinct().Count() == selected.Count && (max is null || selected.Count <= max)
            && selected.All(allowed.Contains);
    }

    private static int? FindElementId(IEnumerable<Category> categories, int questionId) =>
        categories.SelectMany(c => c.Elements).FirstOrDefault(e => e.Question?.Id == questionId)?.Id;
}
