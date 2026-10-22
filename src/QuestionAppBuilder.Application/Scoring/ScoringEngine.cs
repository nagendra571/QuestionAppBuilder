using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Composition;
using System.Text.Json;

namespace QuestionAppBuilder.Application.Scoring;

public sealed class ScoringEngine : IScoringEngine
{
    public SubmissionScore Score(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlySet<int>? applicableQuestionIds = null)
    {
        if (!HasSupportedMaximumScore(graph))
            return ScoreWithZeroAwards(graph, applicableQuestionIds);

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
        var visibleQuestions = applicableQuestionIds
            ?? graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!.Id).ToHashSet();
        var visibleCategories = graph.Categories
            .Where(c => c.Elements.Any(e => e.Question is { } question && visibleQuestions.Contains(question.Id)))
            .Select(c => c.Id).ToHashSet();
        var composition = QuestionAppBuilder.Application.Composition.CompositionEngine.Evaluate(graph, answers, visibleQuestions, visibleCategories);
        var extra = composition.Awards.Aggregate(0, (sum, award) => checked(sum + award.Points));
        var combinedCategories = categoryScores.Select(c => c with { Score = checked(c.Score +
            composition.Awards.Where(a => a.CategoryKey == graph.Categories.Single(x => x.Id == c.CategoryId).LogicalKey)
                .Aggregate(0, (sum, award) => checked(sum + award.Points))) }).ToArray();
        return new SubmissionScore(checked(total + extra), questionScores, combinedCategories, GetEngineVersion(graph));
    }

    private static bool HasSupportedMaximumScore(ApplicationVersionGraph graph)
    {
        long maximum = 0;
        foreach (var question in graph.Categories.SelectMany(c => c.Elements)
                     .Where(e => e.Question is not null).Select(e => e.Question!))
        foreach (var rule in question.Rules)
        {
            if (!ScoringAward.TryGetMaximumAward(question, rule, out var ruleMaximum)) continue;
            if (ruleMaximum > int.MaxValue - maximum) return false;
            maximum += ruleMaximum;
        }

        return true;
    }

    private static SubmissionScore ScoreWithZeroAwards(ApplicationVersionGraph graph, IReadOnlySet<int>? applicableQuestionIds)
    {
        var questionScores = graph.Categories.SelectMany(c => c.Elements)
            .Where(e => e.Kind == ElementKind.Question && e.Question is not null)
            .Select(e => new QuestionScore(e.Question!.Id, 0)
            {
                IsApplicable = applicableQuestionIds is null || applicableQuestionIds.Contains(e.Question.Id),
                Rules = Array.Empty<RuleEvaluation>()
            }).ToArray();
        var categoryScores = graph.Categories
            .Select(c => new CategoryScore(c.Id, 0)).ToArray();
        return new SubmissionScore(0, questionScores, categoryScores, GetEngineVersion(graph));
    }

    private static string GetEngineVersion(ApplicationVersionGraph graph)
    {
        var hasCompositionScoring = false;
        try
        {
            var definition = CompositionDefinition.Read(graph.Version.CompositionJson);
            hasCompositionScoring = definition.Calculations?.Count > 0 || definition.Rules?.Count > 0;
        }
        catch (JsonException) { }
        var hasPointsPerUnit = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null)
            .SelectMany(e => e.Question!.Rules).Any(rule => rule.AwardMethod == AwardMethod.PointsPerUnit);
        return hasPointsPerUnit ? "2.1" : hasCompositionScoring ? "2.0" : "1.0";
    }

    private static (int Points, IReadOnlyList<RuleEvaluation> Trace) ScoreQuestion(Question question, IReadOnlyDictionary<int, AnswerValue> answers)
    {
        var answer = answers.TryGetValue(question.Id, out var found) ? found : new AnswerValue(null, null, null);
        var answered = AnswerSemantics.IsAnswered(question.AnswerType, answer);
        var rules = question.Rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToArray();
        var raw = rules.Select(rule =>
        {
            var matches = TryGetAward(question, rule, answer, answered, out var points);
            return (Rule: rule, Matches: matches, Points: points);
        }).ToArray();
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
            var points = contributes ? raw[i].Points : 0;
            total = checked(total + points);
            trace.Add(new RuleEvaluation(raw[i].Rule.Id, raw[i].Matches, contributes, points));
        }
        return (total, trace);
    }

    private static bool TryGetAward(Question question, ScoringRule rule, AnswerValue answer, bool answered, out int points)
    {
        points = 0;
        if (!ScoringAward.TryGetMaximumAward(question, rule, out _)
            || !RuleMatches(rule, answer, answered)) return false;
        if (rule.AwardMethod == AwardMethod.Fixed)
        {
            points = rule.Points;
            return true;
        }

        if (answer.Text is not null || answer.SelectedOptionIds is not null
            || answer.Number is not decimal value || value < 0 || decimal.Truncate(value) != value
            || question.AnswerType != AnswerType.Number || !question.WholeNumbersOnly
            || question.MinValue is not decimal minimum || minimum < 0 || value < minimum
            || question.MaxValue is decimal maximum && value > maximum) return false;
        var rate = rule.PointsPerUnit;
        if (rate == 0) return true;
        if (rule.MaximumPoints is int cap)
        {
            points = value >= (decimal)cap / rate ? cap : (int)(value * rate);
            return true;
        }
        if (value > (decimal)ScoringAward.MaximumRuleAward / rate) return false;
        points = (int)(value * rate);
        return points <= ScoringAward.MaximumRuleAward;
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
