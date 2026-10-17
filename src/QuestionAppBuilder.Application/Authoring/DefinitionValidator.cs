using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

public sealed class DefinitionValidator
{
    private readonly IDefinitionEvaluator _evaluator;
    private readonly bool _emailDeliveryAvailable;

    public DefinitionValidator(IDefinitionEvaluator evaluator, bool emailDeliveryAvailable = false)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _emailDeliveryAvailable = emailDeliveryAvailable;
    }

    public PublicationReview Review(ApplicationVersionGraph graph, byte[] revision)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(revision);
        var errors = new List<PublicationIssue>();
        var warnings = new List<PublicationIssue>();
        var questions = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!).ToArray();
        if (graph.Categories.Count == 0 || questions.Length == 0)
            errors.Add(new PublicationIssue("empty-definition", null, "Add at least one category and one question before publishing."));
        if (!Enum.IsDefined(graph.Version.RespondentAccessMode))
            errors.Add(new PublicationIssue("invalid-access-mode", null, "Select a supported respondent access mode."));
        if (graph.Version.RespondentAccessMode == RespondentAccessMode.EmailLink && !_emailDeliveryAvailable)
            errors.Add(new PublicationIssue("email-delivery-unavailable", null, "Email-link access requires a configured email sender."));
        if (string.IsNullOrWhiteSpace(graph.Version.DisplayName) || graph.Version.DisplayName.Length > ValidationLimits.NameMaxLength)
            errors.Add(new PublicationIssue("invalid-display-name", null, "The display name is required and must fit the supported length."));

        foreach (var category in graph.Categories)
        {
            if (string.IsNullOrWhiteSpace(category.Name) || category.Name.Length > ValidationLimits.NameMaxLength)
                errors.Add(new PublicationIssue("invalid-category-name", null, "Category names are required and must fit the supported length."));
            foreach (var element in category.Elements)
            {
                if (string.IsNullOrWhiteSpace(element.Label) || element.Label.Length > ValidationLimits.LabelMaxLength)
                    errors.Add(new PublicationIssue("invalid-element-label", element.Id, "Element labels are required and must fit the supported length."));
                if (element.HelpText?.Length > ValidationLimits.HelpTextMaxLength)
                    errors.Add(new PublicationIssue("invalid-help-text", element.Id, "Help text exceeds the supported length."));
                if (element.Question is not { } question) continue;

                var textType = question.AnswerType is AnswerType.Textbox or AnswerType.Textarea;
                var numberType = question.AnswerType == AnswerType.Number;
                var choiceType = question.AnswerType is AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes;
                if (question.MaxLength is not null && (!textType || question.MaxLength is < 1 or > ValidationLimits.AnswerTextMaxLength))
                    errors.Add(new PublicationIssue("invalid-text-limit", element.Id, "Text length settings are invalid for this answer type."));
                if ((question.MinValue is not null || question.MaxValue is not null) && (!numberType || question.MinValue > question.MaxValue))
                    errors.Add(new PublicationIssue("invalid-number-limits", element.Id, "Numeric bounds are invalid for this answer type."));
                if ((question.MinSelections is not null || question.MaxSelections is not null)
                    && (!choiceType || question.MinSelections is < 0 || question.MaxSelections is < 1
                        || question.MinSelections > question.MaxSelections || question.MaxSelections > question.Options.Count))
                    errors.Add(new PublicationIssue("invalid-selection-limits", element.Id, "Selection limits are invalid for this answer type or option set."));
                if (choiceType && question.Options.Count == 0)
                    errors.Add(new PublicationIssue("empty-option-set", element.Id, "Choice questions require at least one option."));
                foreach (var option in question.Options)
                    if (string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > ValidationLimits.OptionLabelMaxLength
                        || option.Value.Length > ValidationLimits.OptionValueMaxLength)
                        errors.Add(new PublicationIssue("invalid-option", element.Id, "Options require a label and must fit the supported length."));

                foreach (var rule in question.Rules)
                {
                    if ((rule.Family == RuleFamily.Selection && (!choiceType || rule.Options.Count == 0
                            || rule.Options.Any(option => question.Options.All(qOption => qOption.Id != option.QuestionOptionId))))
                        || (rule.Family == RuleFamily.NumericRange && !numberType)
                        || (rule.Family == RuleFamily.Presence && rule.Options.Count != 0))
                        errors.Add(new PublicationIssue("invalid-scoring-rule", element.Id, "A scoring rule does not match its question or references an invalid option."));
                }

                AddNumericWarnings(question.Rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToArray(), element.Id, warnings);
            }
        }

        var evaluated = _evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), requireComplete: false);
        errors.AddRange(evaluated.DefinitionIssues.Select(issue => new PublicationIssue(issue.Code, issue.ElementId, issue.Message)));
        return new PublicationReview(revision.ToArray(), errors.DistinctBy(x => (x.Code, x.ElementId)).ToArray(), warnings);
    }

    private static void AddNumericWarnings(IReadOnlyList<ScoringRule> rules, int elementId, ICollection<PublicationIssue> warnings)
    {
        var numeric = rules.Where(r => r.Family == RuleFamily.NumericRange).ToArray();
        for (var current = 0; current < numeric.Length; current++)
        for (var prior = 0; prior < current; prior++)
        {
            var left = numeric[prior];
            var right = numeric[current];
            if (RangesOverlap(left, right))
            {
                var fullyShadowed = (left.MinValue is null || right.MinValue is not null && right.MinValue >= left.MinValue)
                    && (left.MaxValue is null || right.MaxValue is not null && right.MaxValue <= left.MaxValue);
                warnings.Add(new PublicationIssue(fullyShadowed ? "shadowed-numeric-rule" : "overlapping-numeric-rules", elementId,
                    fullyShadowed ? "A numeric rule is unreachable because an earlier range covers it." : "Numeric rules overlap; only the first matching rule contributes."));
                break;
            }
        }
    }

    private static bool RangesOverlap(ScoringRule a, ScoringRule b)
    {
        if (a.MaxValue is decimal aMax && b.MinValue is decimal bMin && (aMax < bMin || aMax == bMin && (!a.MaxInclusive || !b.MinInclusive))) return false;
        if (b.MaxValue is decimal bMax && a.MinValue is decimal aMin && (bMax < aMin || bMax == aMin && (!b.MaxInclusive || !a.MinInclusive))) return false;
        return true;
    }
}
