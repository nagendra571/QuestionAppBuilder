using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain;

public static class ScoringAward
{
    public const int MaximumRuleAward = 1_000_000;

    public static bool TryGetMaximumAward(Question question, ScoringRule rule, out int maximumAward)
    {
        maximumAward = 0;
        if (!Enum.IsDefined(rule.AwardMethod)) return false;
        if (rule.AwardMethod == AwardMethod.Fixed)
        {
            if (rule.Points is < 0 or > MaximumRuleAward) return false;
            maximumAward = rule.Points;
            return true;
        }

        if (rule.Family != RuleFamily.NumericRange || question.AnswerType != AnswerType.Number
            || !question.WholeNumbersOnly || question.MinValue is not decimal minimum || minimum < 0
            || rule.PointsPerUnit is < 0 or > MaximumRuleAward
            || rule.MaximumPoints is < 0 or > MaximumRuleAward)
            return false;

        if (question.MaxValue is null && rule.MaximumPoints is null) return false;
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
        if (boundedAnswer < 0) return false;
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
