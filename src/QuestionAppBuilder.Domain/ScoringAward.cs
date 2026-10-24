using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain;

public static class ScoringAward
{
    public const int MaximumRuleAward = 1_000_000;

    public static string? GetMaximumAwardError(Question question, ScoringRule rule)
    {
        if (!Enum.IsDefined(rule.AwardMethod)) return "Select a supported award method.";
        if (rule.AwardMethod == AwardMethod.Fixed)
            return rule.Points is < 0 or > MaximumRuleAward ? "Rule points must be between 0 and 1,000,000." : null;
        if (rule.Family != RuleFamily.NumericRange) return "Points per unit requires the Numeric range rule family.";
        if (question.AnswerType != AnswerType.Number) return "Points per unit requires a Number question.";
        if (!question.WholeNumbersOnly && (question.MinValue is not decimal configuredMinimum || configuredMinimum < 0))
            return "Enable Whole numbers only and set the question Minimum value to 0 or above in Answer settings.";
        if (!question.WholeNumbersOnly) return "Enable Whole numbers only in the question's Answer settings.";
        if (question.MinValue is not decimal minimum || minimum < 0)
            return "Set the question Minimum value to 0 or above in Answer settings.";
        if (rule.PointsPerUnit is < 0 or > MaximumRuleAward) return "Points per unit must be between 0 and 1,000,000.";
        if (rule.MaximumPoints is < 0 or > MaximumRuleAward) return "Maximum points must be between 0 and 1,000,000.";
        if (question.MaxValue is null && rule.MaximumPoints is null)
            return "Set a question Maximum value or a Maximum points cap to bound the award. Rule Minimum and Maximum values only choose when this rule matches.";
        if (rule.PointsPerUnit > 0 && question.MaxValue is decimal max && decimal.Truncate(max) < 0) return "The question Maximum value must be 0 or above.";
        var answerMaximum = question.MaxValue is decimal bounded ? decimal.Truncate(bounded) : (decimal?)null;
        if (rule.PointsPerUnit > 0 && rule.MaximumPoints is null && answerMaximum is decimal answer
            && answer > (decimal)MaximumRuleAward / rule.PointsPerUnit)
            return "The rate and question Maximum value can exceed 1,000,000 points; lower the rate or set a Maximum points cap.";
        return null;
    }

    public static bool TryGetMaximumAward(Question question, ScoringRule rule, out int maximumAward)
    {
        maximumAward = 0;
        var error = GetMaximumAwardError(question, rule);
        if (error is not null) return false;
        if (rule.AwardMethod == AwardMethod.Fixed)
        {
            maximumAward = rule.Points;
            return true;
        }
        var rate = rule.PointsPerUnit;
        if (rate == 0)
        {
            maximumAward = 0;
            return true;
        }

        if (rule.MaximumPoints is int cap && question.MaxValue is null)
        {
            maximumAward = cap;
            return true;
        }

        var boundedAnswer = decimal.Truncate(question.MaxValue!.Value);
        if (rule.MaximumPoints is int maximumPoints)
        {
            if (boundedAnswer >= (decimal)maximumPoints / rate)
            {
                maximumAward = maximumPoints;
                return true;
            }

            maximumAward = (int)(boundedAnswer * rate);
            return true;
        }

        if (boundedAnswer > (decimal)MaximumRuleAward / rate) return false;
        maximumAward = (int)(boundedAnswer * rate);
        return maximumAward <= MaximumRuleAward;
    }
}
