using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Scoring;

public sealed class ScoringEngine : IScoringEngine
{
    public SubmissionScore Score(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlySet<int>? applicableQuestionIds = null)
    {
        var questionScores = new List<QuestionScore>();
        var categoryScores = new List<CategoryScore>();
        var total = 0;

        foreach (var category in graph.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
        {
            var categoryTotal = 0;
            foreach (var element in category.Elements.OrderBy(e => e.SortOrder).ThenBy(e => e.Id))
            {
                var question = element.Question;
                if (element.Kind != ElementKind.Question || question is null) continue;
                var applicable = applicableQuestionIds is null || applicableQuestionIds.Contains(question.Id);
                var (points, trace) = applicable ? ScoreQuestion(question, answers) : (0, (IReadOnlyList<RuleEvaluation>)Array.Empty<RuleEvaluation>());
                questionScores.Add(new QuestionScore(question.Id, points) { IsApplicable = applicable, Rules = trace });
                categoryTotal = checked(categoryTotal + points);
            }
            categoryScores.Add(new CategoryScore(category.Id, categoryTotal));
            total = checked(total + categoryTotal);
        }
        return new SubmissionScore(total, questionScores, categoryScores, "1.0");
    }

    private static (int Points, IReadOnlyList<RuleEvaluation> Trace) ScoreQuestion(Question question, IReadOnlyDictionary<int, AnswerValue> answers)
    {
        var answer = answers.TryGetValue(question.Id, out var found) ? found : new AnswerValue(null, null, null);
        var answered = AnswerSemantics.IsAnswered(question.AnswerType, answer);
        var rules = question.Rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToArray();
        var raw = rules.Select(rule => (Rule: rule, Matches: rule.Points is >= 0 and <= 1_000_000 && RuleMatches(rule, answer, answered))).ToArray();
        var first = question.ScoringMode == ScoringMode.FirstMatch ? Array.FindIndex(raw, item => item.Matches) : -1;
        var trace = new List<RuleEvaluation>(raw.Length);
        var total = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var contributes = question.ScoringMode switch
            {
                ScoringMode.Additive => raw[i].Matches,
                ScoringMode.FirstMatch => i == first,
                _ => false
            };
            var points = contributes ? raw[i].Rule.Points : 0;
            total = checked(total + points);
            trace.Add(new RuleEvaluation(raw[i].Rule.Id, raw[i].Matches, contributes, points));
        }
        return (total, trace);
    }

    private static bool RuleMatches(ScoringRule rule, AnswerValue answer, bool answered) => rule.Family switch
    {
        RuleFamily.Presence => answered,
        RuleFamily.Selection => SelectionSatisfied(rule, answer),
        RuleFamily.NumericRange => NumericRangeSatisfied(rule, answer),
        _ => false
    };

    private static bool SelectionSatisfied(ScoringRule rule, AnswerValue answer)
    {
        var selected = answer.SelectedOptionIds ?? Array.Empty<int>();
        var ruleOptionIds = rule.Options.Select(option => option.QuestionOptionId).Distinct().ToArray();
        if (selected.Count == 0 || ruleOptionIds.Length == 0) return false;
        return rule.MembershipOperator switch
        {
            MembershipOperator.AnyOf => ruleOptionIds.Any(selected.Contains),
            MembershipOperator.AllOf => ruleOptionIds.All(selected.Contains),
            _ => false
        };
    }

    private static bool NumericRangeSatisfied(ScoringRule rule, AnswerValue answer)
    {
        if (answer.Number is not decimal value) return false;
        var minPass = rule.MinValue is null || (rule.MinInclusive ? value >= rule.MinValue : value > rule.MinValue);
        var maxPass = rule.MaxValue is null || (rule.MaxInclusive ? value <= rule.MaxValue : value < rule.MaxValue);
        return minPass && maxPass;
    }
}
