using FluentAssertions;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Scoring;

public class ScoringDiagnosticsBoundaryTests
{
    [Fact]
    public void Zero_rate_rule_still_wins_first_match_and_hidden_question_has_no_rule_reads()
    {
        var graph = Graph();
        var question = graph.Categories[0].Elements[0].Question!;
        question.ScoringMode = ScoringMode.FirstMatch;
        question.WholeNumbersOnly = true;
        question.MinValue = 0;
        question.Rules =
        [
            new ScoringRule { Id = 1, SortOrder = 1, Family = RuleFamily.NumericRange,
                AwardMethod = AwardMethod.PointsPerUnit, PointsPerUnit = 0, MaximumPoints = 5 },
            new ScoringRule { Id = 2, SortOrder = 2, Family = RuleFamily.Presence, Points = 5 }
        ];
        var answers = new Dictionary<int, AnswerValue> { [1] = new(null, 7, null) };
        var engine = new ScoringEngine();

        var scored = engine.Score(graph, answers);
        scored.Total.Should().Be(0);
        var rules = scored.Diagnostics!.Questions[0].Rules;
        rules[0].Contributes.Should().BeTrue();
        rules[0].Outcome.Should().Be("awarded");
        rules[0].Rate.Should().Be(0);
        rules[0].MatchedPoints.Should().Be(0);
        rules[1].Matches.Should().BeTrue();
        rules[1].Outcome.Should().Be("first-match-suppressed");
        rules[1].SupersedingRuleId.Should().Be(1);

        var hidden = engine.Score(graph, answers, new HashSet<int> { 2 });
        var diagnostic = hidden.Diagnostics!.Questions[0];
        diagnostic.IsApplicable.Should().BeFalse();
        diagnostic.AnswerNumber.Should().BeNull();
        diagnostic.Rules.Should().BeEmpty();
    }

    [Fact]
    public void Combined_diagnostics_keep_visited_prefixes_and_category_scoped_band_winners()
    {
        var graph = Graph();
        var first = graph.Categories[0];
        var second = graph.Categories[1];
        var gated = Rule(first, "Gated", 8);
        gated.Conditions = [Check(0, 1), Check(1, 1)];
        var all = Rule(first, "All", 8);
        all.Checks = [Check(0, 1), Check(1, 1)];
        var any = Rule(first, "Any winner", 0);
        any.AnyCheck = true;
        any.Checks = [Check(0, 1), Check(1, 1), Check(0, 1)];
        any.BandGroup = " band ";
        var suppressed = Rule(first, "Later match", 9);
        suppressed.BandGroup = "BAND";
        var otherCategory = Rule(second, "Independent band", 4);
        otherCategory.BandGroup = "band";
        graph.Version.CompositionJson = new CompositionDefinition
            { Rules = [gated, all, any, suppressed, otherCategory] }.ToJson();

        var score = new ScoringEngine().Score(graph, new Dictionary<int, AnswerValue>());

        score.Total.Should().Be(4);
        score.Categories.Select(c => c.Score).Should().Equal(0, 4);
        var trace = score.Diagnostics!.CombinedRules!;
        trace.Should().HaveCount(5);
        trace[0].Conditions.Should().ContainSingle().Which.Matches.Should().BeFalse();
        trace[0].Checks.Should().BeEmpty();
        trace[1].Checks.Should().ContainSingle().Which.Matches.Should().BeFalse();
        trace[2].Checks.Select(c => c.Matches).Should().Equal(false, true);
        trace[2].Outcome.Should().Be("awarded");
        trace[3].SupersedingRuleId.Should().Be(any.Id);
        trace[3].Outcome.Should().Be("band-suppressed");
        trace[4].Outcome.Should().Be("awarded");
        trace[4].SupersedingRuleId.Should().BeNull();
    }

    [Fact]
    public void Unavailable_calculation_fails_not_equal_and_no_visible_target_skips_reads()
    {
        var graph = Graph();
        var category = graph.Categories[0];
        var calculation = new CalculatedField
        {
            Name = "Undefined percentage", CategoryKey = category.LogicalKey,
            Operation = CalculationOperation.Percentage,
            Sources = [new() { Value = 1 }, new() { Value = 0 }]
        };
        var rule = Rule(category, "Unavailable value", 9);
        rule.Checks = [new()
        {
            Left = new() { Kind = SourceKind.Calculation, Key = calculation.Id },
            Comparison = Comparison.NotEqual, Right = new() { Value = 1 }
        }];
        graph.Version.CompositionJson = new CompositionDefinition
            { Calculations = [calculation], Rules = [rule] }.ToJson();

        var engine = new ScoringEngine();
        var score = engine.Score(graph, new Dictionary<int, AnswerValue>());
        score.Total.Should().Be(0);
        var trace = score.Diagnostics!.CombinedRules!.Single();
        trace.Outcome.Should().Be("checks-not-matched");
        trace.Checks.Single().Left.Value.Should().BeNull();
        trace.Checks.Single().Matches.Should().BeFalse();
        trace.Checks.Single().Left.Effects.Should().Contain(e => e.Code == "unavailable-calculation");

        var hidden = engine.Score(graph, new Dictionary<int, AnswerValue>(), new HashSet<int>());
        var skipped = hidden.Diagnostics!.CombinedRules!.Single();
        skipped.Outcome.Should().Be("no-visible-target");
        skipped.TargetQuestionId.Should().BeNull();
        skipped.Conditions.Should().BeEmpty();
        skipped.Checks.Should().BeEmpty();
    }

    [Fact]
    public void Later_invalid_combined_rule_discards_early_awards_but_preserves_question_diagnostics()
    {
        var graph = Graph();
        var category = graph.Categories[0];
        category.Elements[0].Question!.Rules =
            [new ScoringRule { Id = 1, Family = RuleFamily.Presence, Points = 3 }];
        var valid = Rule(category, "Early award", 8);
        var invalid = Rule(category, "", 9);
        graph.Version.CompositionJson = new CompositionDefinition { Rules = [valid, invalid] }.ToJson();

        var score = new ScoringEngine().Score(graph,
            new Dictionary<int, AnswerValue> { [1] = new(null, 2, null) });

        score.Total.Should().Be(3);
        score.Questions[0].Points.Should().Be(3);
        score.Diagnostics!.Questions[0].Rules.Single().Outcome.Should().Be("awarded");
        score.Diagnostics.CombinedRules.Should().BeNull();
        score.Diagnostics.CombinedDefinitionErrors.Should().NotBeEmpty();
        score.Diagnostics.UnavailableReason.Should().BeNull();
    }

    private static ValueCheck Check(decimal left, decimal right) => new()
    {
        Left = new() { Value = left }, Comparison = Comparison.Equal, Right = new() { Value = right }
    };

    private static CompositionRule Rule(Category category, string name, int points) => new()
    {
        Name = name, Kind = RulePurpose.Score, CategoryKey = category.LogicalKey,
        Points = points, Checks = [Check(1, 1)]
    };

    private static ApplicationVersionGraph Graph()
    {
        var categories = Enumerable.Range(1, 2).Select(id => new Category
        {
            Id = id, SortOrder = id, Name = $"Category {id}",
            Elements = [new Element
            {
                Id = id, Kind = ElementKind.Question, Label = $"Question {id}",
                Question = new Question { Id = id, AnswerType = AnswerType.Number }
            }]
        }).ToList();
        return new ApplicationVersionGraph
            { Version = new ApplicationVersion { Id = 1, Categories = categories }, Categories = categories };
    }
}
