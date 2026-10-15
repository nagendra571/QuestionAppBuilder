using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Scoring;

public sealed class ScoringEngine : IScoringEngine
{
    public SubmissionScore Score(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers)
    {
        var questionScores = new List<QuestionScore>();
        var categoryScores = new List<CategoryScore>();

        foreach (var category in graph.Categories)
        {
            var categoryTotal = 0;

            foreach (var element in category.Elements)
            {
                if (element.Kind != ElementKind.Question || element.Question is null)
                {
                    continue;
                }

                var points = ScoreQuestion(element.Question, answers);
                questionScores.Add(new QuestionScore(element.Question.Id, points));
                categoryTotal += points;
            }

            categoryScores.Add(new CategoryScore(category.Id, categoryTotal));
        }

        var total = questionScores.Sum(questionScore => questionScore.Points);

        return new SubmissionScore(total, questionScores, categoryScores);
    }

    private static int ScoreQuestion(Question question, IReadOnlyDictionary<int, AnswerValue> answers)
    {
        var answer = answers.TryGetValue(question.Id, out var found)
            ? found
            : new AnswerValue(null, null, null);

        var isAnswered = IsAnswered(question.AnswerType, answer);

        var points = 0;
        foreach (var rule in question.Rules)
        {
            var fires = rule.Family switch
            {
                RuleFamily.Presence => isAnswered,
                RuleFamily.Selection => SelectionSatisfied(rule, answer),
                RuleFamily.NumericRange => NumericRangeSatisfied(rule, answer),
                _ => false
            };

            if (fires)
            {
                points += rule.Points;
            }
        }

        return points;
    }

    private static bool IsAnswered(AnswerType answerType, AnswerValue answer) => answerType switch
    {
        AnswerType.Textbox or AnswerType.Textarea => !string.IsNullOrWhiteSpace(answer.Text),
        AnswerType.Number => answer.Number.HasValue,
        AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes => answer.SelectedOptionIds is { Count: > 0 },
        _ => false
    };

    private static bool SelectionSatisfied(ScoringRule rule, AnswerValue answer)
    {
        var selected = answer.SelectedOptionIds ?? Array.Empty<int>();
        var ruleOptionIds = rule.Options.Select(option => option.QuestionOptionId);

        return rule.MembershipOperator switch
        {
            MembershipOperator.AnyOf => ruleOptionIds.Any(selected.Contains),
            MembershipOperator.AllOf => ruleOptionIds.All(selected.Contains),
            _ => false
        };
    }

    private static bool NumericRangeSatisfied(ScoringRule rule, AnswerValue answer)
    {
        if (answer.Number is not decimal value)
        {
            return false;
        }

        return (rule.MinValue is null || value >= rule.MinValue)
            && (rule.MaxValue is null || value <= rule.MaxValue);
    }
}
