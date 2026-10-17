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
}
