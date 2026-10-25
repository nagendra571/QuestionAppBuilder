using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Application.Composition;
using System.Text.Json;

namespace QuestionAppBuilder.Application.Scoring;

public sealed class ScoringEngine : IScoringEngine
{
    private sealed record AwardAttempt(bool Matches, int Points, string Outcome, decimal? Units,
        int? Rate, int? Cap, bool CapReached);

    public SubmissionScore Score(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlySet<int>? applicableQuestionIds = null)
    {
        if (!HasSupportedMaximumScore(graph))
            return ScoreWithZeroAwards(graph, applicableQuestionIds) with
            {
                Diagnostics = new ScoringDiagnostics([], null,
                    "Scoring explanations are unavailable because the configured maximum award exceeds the supported total.")
            };

        var questionScores = new List<QuestionScore>();
        var questionDiagnostics = new List<QuestionScoringDiagnostic>();
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
                var answer = answers.TryGetValue(question.Id, out var found) ? found : new AnswerValue(null, null, null);
                var answered = applicable && AnswerSemantics.IsAnswered(question.AnswerType, answer);
                var scored = applicable ? ScoreQuestion(question, answer, answered)
                    : (0, (IReadOnlyList<RuleEvaluation>)Array.Empty<RuleEvaluation>(), (IReadOnlyList<QuestionScoringRuleDiagnostic>)Array.Empty<QuestionScoringRuleDiagnostic>());
                questionScores.Add(new QuestionScore(question.Id, scored.Item1) { IsApplicable = applicable, Rules = scored.Item2 });
                questionDiagnostics.Add(new QuestionScoringDiagnostic(question.Id, applicable, answered,
                    applicable ? answer.Number : null,
                    applicable ? answer.SelectedOptionIds?.ToArray() ?? [] : [], scored.Item3));
                categoryTotal = checked(categoryTotal + scored.Item1);
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
        return new SubmissionScore(checked(total + extra), questionScores, combinedCategories, GetEngineVersion(graph))
        {
            Diagnostics = new ScoringDiagnostics(questionDiagnostics, composition.CombinedScoreRules,
                CombinedDefinitionErrors: composition.DefinitionErrors.Count == 0 ? [] : composition.DefinitionErrors)
        };
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

    private static (int Points, IReadOnlyList<RuleEvaluation> Trace, IReadOnlyList<QuestionScoringRuleDiagnostic> Diagnostics)
        ScoreQuestion(Question question, AnswerValue answer, bool answered)
    {
        var rules = question.Rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToArray();
        var raw = rules.Select(rule => (Rule: rule, Attempt: TryGetAward(question, rule, answer, answered))).ToArray();
        var first = question.ScoringMode == ScoringMode.FirstMatch ? Array.FindIndex(raw, item => item.Attempt.Matches) : -1;
        var trace = new List<RuleEvaluation>(raw.Length);
        var diagnostics = new List<QuestionScoringRuleDiagnostic>(raw.Length);
        var total = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var contributes = question.ScoringMode switch
            {
                ScoringMode.Additive => raw[i].Attempt.Matches,
                ScoringMode.FirstMatch => i == first,
                _ => false
            };
            var points = contributes ? raw[i].Attempt.Points : 0;
            total = checked(total + points);
            trace.Add(new RuleEvaluation(raw[i].Rule.Id, raw[i].Attempt.Matches, contributes, points));
            var outcome = raw[i].Attempt.Outcome;
            int? supersedingRuleId = null;
            if (raw[i].Attempt.Matches && contributes) outcome = "awarded";
            else if (raw[i].Attempt.Matches && question.ScoringMode == ScoringMode.FirstMatch)
            {
                outcome = "first-match-suppressed";
                supersedingRuleId = first >= 0 ? raw[first].Rule.Id : null;
            }
            else if (raw[i].Attempt.Matches && !Enum.IsDefined(question.ScoringMode)) outcome = "unsupported-scoring-mode";
            diagnostics.Add(new QuestionScoringRuleDiagnostic(raw[i].Rule.Id, outcome,
                raw[i].Attempt.Matches ? raw[i].Attempt.Points : null, raw[i].Attempt.Units,
                raw[i].Attempt.Rate, raw[i].Attempt.Cap, raw[i].Attempt.CapReached,
                raw[i].Attempt.Matches, contributes, points, supersedingRuleId));
        }
        return (total, trace, diagnostics);
    }

    private static AwardAttempt TryGetAward(Question question, ScoringRule rule, AnswerValue answer, bool answered)
    {
        var units = rule.AwardMethod == AwardMethod.PointsPerUnit ? answer.Number : null;
        var configuredRate = rule.AwardMethod == AwardMethod.PointsPerUnit ? rule.PointsPerUnit : (int?)null;
        var configuredCap = rule.AwardMethod == AwardMethod.PointsPerUnit ? rule.MaximumPoints : null;
        if (!ScoringAward.TryGetMaximumAward(question, rule, out _))
            return new(false, 0, "invalid-award-configuration", units, configuredRate, configuredCap, false);
        if (!RuleMatches(rule, answer, answered))
            return new(false, 0, "condition-not-matched", units, configuredRate, configuredCap, false);
        if (rule.AwardMethod == AwardMethod.Fixed)
            return new(true, rule.Points, "matched", null, null, null, false);

        if (answer.Text is not null || answer.SelectedOptionIds is not null
            || answer.Number is not decimal value || value < 0 || decimal.Truncate(value) != value
            || question.AnswerType != AnswerType.Number || !question.WholeNumbersOnly
            || question.MinValue is not decimal minimum || minimum < 0 || value < minimum
            || question.MaxValue is decimal maximum && value > maximum)
            return new(false, 0, "invalid-per-unit-answer", units, configuredRate, configuredCap, false);
        var rate = rule.PointsPerUnit;
        if (rate == 0) return new(true, 0, "matched", units, rate, configuredCap, false);
        if (rule.MaximumPoints is int cap)
        {
            var capReached = value >= (decimal)cap / rate;
            var points = capReached ? cap : (int)(value * rate);
            return new(true, points, "matched", units, rate, cap, capReached);
        }
        if (value > (decimal)ScoringAward.MaximumRuleAward / rate)
            return new(false, 0, "per-unit-limit", units, rate, null, false);
        var uncappedPoints = (int)(value * rate);
        return uncappedPoints <= ScoringAward.MaximumRuleAward
            ? new(true, uncappedPoints, "matched", units, rate, null, false)
            : new(false, 0, "per-unit-limit", units, rate, null, false);
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
