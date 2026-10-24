using FluentAssertions;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Authoring;

public class DefinitionValidatorTests
{
    [Fact]
    public void Forward_reference_after_reorder_blocks_publish()
    {
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { RespondentAccessMode = RespondentAccessMode.HostSignIn },
            Categories = [new Category
            {
                Id = 1,
                Name = "Basics",
                Elements =
                [
                    new Element { Id = 10, Kind = ElementKind.Question, Label = "Dependent", VisibilityConditionId = 1, Question = new Question { Id = 100, ElementId = 10, AnswerType = AnswerType.Textbox } },
                    new Element { Id = 11, Kind = ElementKind.Question, Label = "Source", Question = new Question { Id = 101, ElementId = 11, AnswerType = AnswerType.Number } }
                ]
            }],
            VisibilityConditions = [new VisibilityCondition
            {
                Id = 1,
                Predicates = [new VisibilityPredicate { SourceQuestionId = 101, Operator = ConditionOperator.GreaterThan, NumberValue = 0 }]
            }]
        };

        var review = new DefinitionValidator(new DefinitionEvaluator()).Review(graph, new byte[] { 1 });

        review.Errors.Select(x => x.Code).Should().Contain("forward-condition-reference");
    }

    [Fact]
    public void Numeric_overlap_is_a_warning_and_empty_definition_is_blocked()
    {
        var question = new Question { Id = 1, ElementId = 1, AnswerType = AnswerType.Number, ScoringMode = ScoringMode.FirstMatch };
        question.Rules.AddRange(
        [
            new ScoringRule { Id = 1, Family = RuleFamily.NumericRange, Points = 1, MinValue = 0, MaxValue = 10, SortOrder = 1 },
            new ScoringRule { Id = 2, Family = RuleFamily.NumericRange, Points = 2, MinValue = 5, MaxValue = 15, SortOrder = 2 }
        ]);
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { DisplayName = "Numbers" },
            Categories = [new Category { Id = 1, Name = "Numbers", Elements = [new Element { Id = 1, Kind = ElementKind.Question, Label = "Score", Question = question }] }]
        };

        var review = new DefinitionValidator(new DefinitionEvaluator()).Review(graph, new byte[] { 1 });

        review.Errors.Should().BeEmpty();
        review.Warnings.Select(x => x.Code).Should().Contain("overlapping-numeric-rules");
        new DefinitionValidator(new DefinitionEvaluator()).Review(new ApplicationVersionGraph { Version = new ApplicationVersion(), Categories = [] }, new byte[] { 1 })
            .Errors.Select(x => x.Code).Should().Contain("empty-definition");
    }

    [Fact]
    public void Points_per_unit_rules_require_safe_whole_number_numeric_bounds()
    {
        var invalidConfigurations = new (string Name, Action<Question, ScoringRule> Mutate)[]
        {
            ("a nonnumeric question", (question, _) => question.AnswerType = AnswerType.Textbox),
            ("a fractional question", (question, _) => question.WholeNumbersOnly = false),
            ("a missing minimum", (question, _) => question.MinValue = null),
            ("a negative minimum", (question, _) => question.MinValue = -1),
            ("a negative rate", (_, rule) => rule.PointsPerUnit = -1),
            ("a nonnumeric rule family", (_, rule) => rule.Family = RuleFamily.Presence),
            ("no answer maximum or cap", (question, rule) => { question.MaxValue = null; rule.MaximumPoints = null; }),
            ("an unbounded unsafe award", (question, rule) => { question.MaxValue = 2_000_000; rule.PointsPerUnit = 1; rule.MaximumPoints = null; }),
            ("a negative cap", (_, rule) => rule.MaximumPoints = -1)
        };

        foreach (var (name, mutate) in invalidConfigurations)
        {
            var question = new Question
            {
                Id = 1, ElementId = 1, AnswerType = AnswerType.Number, WholeNumbersOnly = true,
                MinValue = 0, MaxValue = 10
            };
            var rule = new ScoringRule
            {
                Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
                PointsPerUnit = 2, MaximumPoints = 20, MinValue = 0, MaxValue = 10
            };
            question.Rules.Add(rule);
            mutate(question, rule);
            var graph = new ApplicationVersionGraph
            {
                Version = new ApplicationVersion { DisplayName = "Safe scoring" },
                Categories = [new Category
                {
                    Id = 1, Name = "Numbers",
                    Elements = [new Element { Id = 1, Kind = ElementKind.Question, Label = "Units", Question = question }]
                }]
            };

            var review = new DefinitionValidator(new DefinitionEvaluator()).Review(graph, new byte[] { 1 });

            review.Errors.Select(issue => issue.Code).Should().Contain("invalid-points-per-unit", name);
        }
    }

    [Fact]
    public void Aggregate_points_per_unit_bound_overflow_is_a_publish_error()
    {
        var question = new Question
        {
            Id = 1, ElementId = 1, AnswerType = AnswerType.Number, WholeNumbersOnly = true,
            MinValue = 0, MaxValue = 1_000_000
        };
        question.Rules.AddRange(Enumerable.Range(1, 2148).Select(id => new ScoringRule
        {
            Id = id, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 1, MaximumPoints = 1_000_000, MinValue = 0
        }));
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { DisplayName = "Overflow" },
            Categories = [new Category
            {
                Id = 1, Name = "Numbers",
                Elements = [new Element { Id = 1, Kind = ElementKind.Question, Label = "Units", Question = question }]
            }]
        };

        var review = new DefinitionValidator(new DefinitionEvaluator()).Review(graph, new byte[] { 1 });

        review.Errors.Select(issue => issue.Code).Should().Contain("total-points-overflow");
    }

    [Fact]
    public void Per_unit_rule_may_leave_predicate_bounds_blank_when_question_or_cap_bounds_the_award()
    {
        var question = new Question
        {
            Id = 1, ElementId = 1, AnswerType = AnswerType.Number, WholeNumbersOnly = true,
            MinValue = 0, MaxValue = 100,
            Rules = [new ScoringRule
            {
                Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
                PointsPerUnit = 2, MaximumPoints = 200
            }]
        };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { DisplayName = "Bounded units" },
            Categories = [new Category { Id = 1, Name = "Numbers", Elements =
                [new Element { Id = 1, Kind = ElementKind.Question, Label = "Units", Question = question }] }]
        };

        var review = new DefinitionValidator(new DefinitionEvaluator()).Review(graph, new byte[] { 1 });

        review.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("100", null)]
    [InlineData(null, 200)]
    public void Per_unit_rule_is_valid_when_either_question_maximum_or_award_cap_bounds_it(string? questionMaximum, int? awardCap)
    {
        var question = new Question
        {
            Id = 1, ElementId = 1, AnswerType = AnswerType.Number, WholeNumbersOnly = true,
            MinValue = 0, MaxValue = questionMaximum is null ? null : decimal.Parse(questionMaximum, System.Globalization.CultureInfo.InvariantCulture),
            Rules = [new ScoringRule
            {
                Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
                PointsPerUnit = 2, MaximumPoints = awardCap
            }]
        };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { DisplayName = "Bounded units" },
            Categories = [new Category { Id = 1, Name = "Numbers", Elements =
                [new Element { Id = 1, Kind = ElementKind.Question, Label = "Units", Question = question }] }]
        };

        var review = new DefinitionValidator(new DefinitionEvaluator()).Review(graph, new byte[] { 1 });

        review.Errors.Should().BeEmpty();
    }
}
