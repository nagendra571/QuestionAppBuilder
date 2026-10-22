using FluentAssertions;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Scoring;

public class ScoringEngineTests
{
    private readonly IScoringEngine _engine = new ScoringEngine();

    [Fact]
    public void Presence_fires_only_when_answered()
    {
        var graph = BuildGraph(CategoryWith(1, QuestionElement(1, AnswerType.Textbox, PresenceRule(1))));

        _engine.Score(graph, Answers((1, new AnswerValue("abc", null, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(1);
        _engine.Score(graph, Answers((1, new AnswerValue("   ", null, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(0);
        _engine.Score(graph, Answers((1, new AnswerValue(null, null, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(0);
    }

    [Fact]
    public void Presence_counts_zero_number_as_answered()
    {
        var graph = BuildGraph(CategoryWith(1, QuestionElement(1, AnswerType.Number, PresenceRule(2))));

        _engine.Score(graph, Answers((1, new AnswerValue(null, 0m, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(2);
    }

    [Fact]
    public void Selection_any_of_fires_on_membership()
    {
        var graph = BuildGraph(CategoryWith(1,
            QuestionElementWithOptions(1, AnswerType.Dropdown, [11, 12, 13],
                SelectionRule(5, MembershipOperator.AnyOf, 11, 12))));

        _engine.Score(graph, Answers((1, new AnswerValue(null, null, [12]))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(5);
        _engine.Score(graph, Answers((1, new AnswerValue(null, null, [13]))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(0);
    }

    [Fact]
    public void Selection_all_of_requires_every_option()
    {
        var graph = BuildGraph(CategoryWith(1,
            QuestionElementWithOptions(1, AnswerType.Checkboxes, [21, 22],
                SelectionRule(3, MembershipOperator.AllOf, 21, 22))));

        _engine.Score(graph, Answers((1, new AnswerValue(null, null, [21, 22]))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(3);
        _engine.Score(graph, Answers((1, new AnswerValue(null, null, [21]))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(0);
    }

    [Fact]
    public void Numeric_tiers_are_inclusive_at_both_bounds()
    {
        var graph = BuildGraph(CategoryWith(1,
            QuestionElement(1, AnswerType.Number, NumericRule(2, 26m, null), NumericRule(1, 10m, 25m))));

        _engine.Score(graph, Answers((1, new AnswerValue(null, 26m, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(2);
        _engine.Score(graph, Answers((1, new AnswerValue(null, 25m, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(1);
        _engine.Score(graph, Answers((1, new AnswerValue(null, 10m, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(1);
        _engine.Score(graph, Answers((1, new AnswerValue(null, 9m, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(0);
    }

    [Fact]
    public void First_match_uses_order_and_exclusive_lower_bound()
    {
        var exclusive = NumericRule(2, 35m, null);
        exclusive.MinInclusive = false;
        exclusive.SortOrder = 1;
        var graph = BuildGraph(CategoryWith(1,
            QuestionElementWithOptions(1, AnswerType.Number, [], ScoringMode.FirstMatch, exclusive)));

        _engine.Score(graph, Answers((1, new AnswerValue(null, 35m, null)))).Questions.Single().Points.Should().Be(0);
        _engine.Score(graph, Answers((1, new AnswerValue(null, 36m, null)))).Questions.Single().Points.Should().Be(2);
    }

    [Fact]
    public void First_match_trace_separates_matching_from_contributing_rules()
    {
        var first = NumericRule(3, 0m, 100m);
        first.SortOrder = 1;
        var second = NumericRule(2, 0m, 200m);
        second.SortOrder = 2;
        var graph = BuildGraph(CategoryWith(1, QuestionElementWithOptions(1, AnswerType.Number, [], ScoringMode.FirstMatch, first, second)));

        var result = _engine.Score(graph, Answers((1, new AnswerValue(null, 50m, null))));

        result.Total.Should().Be(3);
        result.EngineVersion.Should().Be("1.0");
        result.Questions.Single().Rules.Should().SatisfyRespectively(
            rule => { rule.Matches.Should().BeTrue(); rule.Contributes.Should().BeTrue(); rule.Points.Should().Be(3); },
            rule => { rule.Matches.Should().BeTrue(); rule.Contributes.Should().BeFalse(); rule.Points.Should().Be(0); });
    }

    [Fact]
    public void Rules_are_additive()
    {
        var graph = BuildGraph(CategoryWith(1,
            QuestionElementWithOptions(1, AnswerType.Checkboxes, [31, 32],
                SelectionRule(1, MembershipOperator.AnyOf, 31),
                SelectionRule(2, MembershipOperator.AnyOf, 32))));

        _engine.Score(graph, Answers((1, new AnswerValue(null, null, [31, 32]))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(3);
    }

    [Fact]
    public void Overlapping_rules_all_fire()
    {
        var graph = BuildGraph(CategoryWith(1,
            QuestionElement(1, AnswerType.Number, NumericRule(1, 5m, null), NumericRule(2, 5m, 10m))));

        _engine.Score(graph, Answers((1, new AnswerValue(null, 7m, null))))
            .Questions.Single(q => q.QuestionId == 1).Points.Should().Be(3);
    }

    [Fact]
    public void Points_per_unit_calculates_integer_answers_and_applies_the_cap()
    {
        var rule = new ScoringRule
        {
            Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 3, MaximumPoints = 18, MinValue = 0, MaxValue = 100
        };
        var element = QuestionElement(1, AnswerType.Number, rule);
        element.Question!.WholeNumbersOnly = true;
        element.Question.MinValue = 0;
        element.Question.MaxValue = 100;
        var graph = BuildGraph(CategoryWith(1, element));

        var belowCap = _engine.Score(graph, Answers((1, new AnswerValue(null, 5m, null))));
        var atCap = _engine.Score(graph, Answers((1, new AnswerValue(null, 9m, null))));

        belowCap.Questions.Single().Points.Should().Be(15);
        belowCap.Questions.Single().Rules.Single().Points.Should().Be(15);
        atCap.Questions.Single().Points.Should().Be(18);
        atCap.EngineVersion.Should().Be("2.1");
    }

    [Fact]
    public void Points_per_unit_does_not_score_blank_invalid_or_hidden_answers()
    {
        var rule = new ScoringRule
        {
            Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 2, MaximumPoints = 50, MinValue = 0, MaxValue = 100
        };
        var element = QuestionElement(1, AnswerType.Number, rule);
        element.Question!.WholeNumbersOnly = true;
        element.Question.MinValue = 0;
        element.Question.MaxValue = 100;
        var graph = BuildGraph(CategoryWith(1, element));

        _engine.Score(graph, Answers((1, new AnswerValue(null, null, null)))).Questions.Single().Points.Should().Be(0);
        _engine.Score(graph, Answers((1, new AnswerValue(null, -1m, null)))).Questions.Single().Points.Should().Be(0);
        _engine.Score(graph, Answers((1, new AnswerValue(null, 1.5m, null)))).Questions.Single().Points.Should().Be(0);
        _engine.Score(graph, Answers((1, new AnswerValue("unexpected text", 10m, null)))).Questions.Single().Points.Should().Be(0);
        _engine.Score(graph, Answers((1, new AnswerValue(null, 10m, [999])))).Questions.Single().Points.Should().Be(0);
        var hidden = _engine.Score(graph, Answers((1, new AnswerValue(null, 10m, null))), new HashSet<int>());
        hidden.Questions.Single().IsApplicable.Should().BeFalse();
        hidden.Questions.Single().Points.Should().Be(0);
        hidden.Questions.Single().Rules.Should().BeEmpty();
    }

    [Fact]
    public void Points_per_unit_respects_additive_and_first_match_modes()
    {
        var first = new ScoringRule
        {
            Id = 1, SortOrder = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 2, MaximumPoints = 100, MinValue = 0, MaxValue = 10
        };
        var second = new ScoringRule
        {
            Id = 2, SortOrder = 2, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 3, MaximumPoints = 100, MinValue = 0, MaxValue = 20
        };
        var additiveElement = QuestionElement(1, AnswerType.Number, first, second);
        additiveElement.Question!.WholeNumbersOnly = true;
        additiveElement.Question.MinValue = 0;
        additiveElement.Question.MaxValue = 20;
        var additive = _engine.Score(BuildGraph(CategoryWith(1, additiveElement)), Answers((1, new AnswerValue(null, 5m, null))));
        additive.Questions.Single().Points.Should().Be(25);
        additive.Questions.Single().Rules.Select(r => r.Contributes).Should().Equal(true, true);

        var firstMatchElement = QuestionElementWithOptions(1, AnswerType.Number, [], ScoringMode.FirstMatch, first, second);
        firstMatchElement.Question!.WholeNumbersOnly = true;
        firstMatchElement.Question.MinValue = 0;
        firstMatchElement.Question.MaxValue = 20;
        var firstMatch = _engine.Score(BuildGraph(CategoryWith(1, firstMatchElement)), Answers((1, new AnswerValue(null, 5m, null))));
        firstMatch.Questions.Single().Points.Should().Be(10);
        firstMatch.Questions.Single().Rules.Select(r => r.Contributes).Should().Equal(true, false);
        firstMatch.Questions.Single().Rules.Select(r => r.Points).Should().Equal(10, 0);
    }

    [Fact]
    public void Capped_large_answers_are_multiplication_safe()
    {
        var rule = new ScoringRule
        {
            Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 1_000_000, MaximumPoints = 500, MinValue = 0
        };
        var element = QuestionElement(1, AnswerType.Number, rule);
        element.Question!.WholeNumbersOnly = true;
        element.Question.MinValue = 0;
        element.Question.MaxValue = decimal.MaxValue;
        var score = _engine.Score(BuildGraph(CategoryWith(1, element)), Answers((1, new AnswerValue(null, decimal.MaxValue, null))));

        score.Total.Should().Be(500);
        score.Questions.Single().Rules.Single().Points.Should().Be(500);
    }

    [Fact]
    public void Zero_rate_with_a_cap_has_a_zero_maximum_and_scores_zero()
    {
        var rule = new ScoringRule
        {
            Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 0, MaximumPoints = 500, MinValue = 0
        };
        var element = QuestionElement(1, AnswerType.Number, rule);
        element.Question!.WholeNumbersOnly = true;
        element.Question.MinValue = 0;
        var graph = BuildGraph(CategoryWith(1, element));
        var result = new DefinitionEvaluator().Evaluate(graph, Answers((1, new AnswerValue(null, 10m, null))), true);

        result.DefinitionIssues.Should().BeEmpty();
        result.Score.Total.Should().Be(0);
        result.Score.EngineVersion.Should().Be("2.1");
    }

    [Fact]
    public void Content_elements_are_ignored()
    {
        var graph = BuildGraph(CategoryWith(1,
            ContentElement(1),
            QuestionElement(2, AnswerType.Textbox, PresenceRule(1))));

        var score = _engine.Score(graph, Answers((2, new AnswerValue("abc", null, null))));

        score.Questions.Should().ContainSingle()
            .Which.QuestionId.Should().Be(2);
        score.Total.Should().Be(1);
    }

    [Fact]
    public void Unanswered_questions_score_zero_but_appear()
    {
        var graph = BuildGraph(CategoryWith(1,
            QuestionElement(1, AnswerType.Textbox, PresenceRule(1)),
            QuestionElement(2, AnswerType.Textbox, PresenceRule(1))));

        var score = _engine.Score(graph, Answers((1, new AnswerValue("abc", null, null))));

        score.Questions.Should().HaveCount(2);
        score.Questions.Single(q => q.QuestionId == 1).Points.Should().Be(1);
        score.Questions.Single(q => q.QuestionId == 2).Points.Should().Be(0);
    }

    [Fact]
    public void Category_totals_and_grand_total()
    {
        var graph = BuildGraph(
            CategoryWith(1,
                QuestionElement(1, AnswerType.Textbox, PresenceRule(2)),
                QuestionElement(2, AnswerType.Textbox, PresenceRule(3))),
            CategoryWith(2,
                QuestionElement(3, AnswerType.Number, PresenceRule(4))));

        var score = _engine.Score(graph, Answers(
            (1, new AnswerValue("abc", null, null)),
            (3, new AnswerValue(null, 10m, null))));

        score.Categories.Single(c => c.CategoryId == 1).Score.Should().Be(2);
        score.Categories.Single(c => c.CategoryId == 2).Score.Should().Be(4);
        score.Questions.Single(q => q.QuestionId == 2).Points.Should().Be(0);
        score.Total.Should().Be(6);
    }

    private static ApplicationVersionGraph BuildGraph(params Category[] categories) => new()
    {
        Version = new ApplicationVersion { Id = 1, ApplicationId = 1, VersionNumber = 1 },
        Categories = categories
    };

    private static Category CategoryWith(int id, params Element[] elements) => new()
    {
        Id = id,
        ApplicationVersionId = 1,
        Name = $"Category {id}",
        Elements = elements.ToList()
    };

    private static Element ContentElement(int id) => new()
    {
        Id = id,
        Kind = ElementKind.Content,
        Label = $"Content {id}"
    };

    private static Element QuestionElement(int id, AnswerType answerType, params ScoringRule[] rules) =>
        QuestionElementWithOptions(id, answerType, [], rules);

    private static Element QuestionElementWithOptions(int id, AnswerType answerType, int[] optionIds, params ScoringRule[] rules) =>
        QuestionElementWithOptions(id, answerType, optionIds, ScoringMode.Additive, rules);

    private static Element QuestionElementWithOptions(int id, AnswerType answerType, int[] optionIds, ScoringMode scoringMode, params ScoringRule[] rules) => new()
    {
        Id = id,
        Kind = ElementKind.Question,
        Label = $"Question {id}",
        Question = new Question
        {
            Id = id,
            ElementId = id,
            AnswerType = answerType,
            ScoringMode = scoringMode,
            Rules = rules.ToList(),
            Options = optionIds.Select(optionId => new QuestionOption { Id = optionId, QuestionId = id }).ToList()
        }
    };

    private static ScoringRule PresenceRule(int points) => new()
    {
        Family = RuleFamily.Presence,
        Points = points
    };

    private static ScoringRule SelectionRule(int points, MembershipOperator membershipOperator, params int[] questionOptionIds) => new()
    {
        Family = RuleFamily.Selection,
        Points = points,
        MembershipOperator = membershipOperator,
        Options = questionOptionIds.Select(id => new ScoringRuleOption { QuestionOptionId = id }).ToList()
    };

    private static ScoringRule NumericRule(int points, decimal? minValue, decimal? maxValue) => new()
    {
        Family = RuleFamily.NumericRange,
        Points = points,
        MinValue = minValue,
        MaxValue = maxValue
    };

    private static Dictionary<int, AnswerValue> Answers(params (int QuestionId, AnswerValue Value)[] entries) =>
        entries.ToDictionary(entry => entry.QuestionId, entry => entry.Value);
}
