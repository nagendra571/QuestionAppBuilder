using FluentAssertions;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Evaluation;

public class DefinitionEvaluatorTests
{
    private readonly IDefinitionEvaluator _evaluator = new DefinitionEvaluator();

    [Fact]
    public void Hidden_source_values_do_not_reveal_dependents()
    {
        var graph = Graph(
            Category(1, Question(1, AnswerType.Dropdown, options: [10]),
                Question(2, AnswerType.Dropdown, options: [20], conditionId: 1)),
            CategoryWithCondition(2, 2, Question(3, AnswerType.Textbox)));
        graph.VisibilityConditions =
        [
            Condition(1, Predicate(1, ConditionOperator.HasAnyOption, 10)),
            Condition(2, Predicate(2, ConditionOperator.HasAnyOption, 20))
        ];

        var result = _evaluator.Evaluate(graph, Answers((1, new AnswerValue(null, null, [])), (2, new AnswerValue(null, null, [20]))), requireComplete: false);

        result.VisibleQuestionIds.Should().Contain(1).And.NotContain(2).And.NotContain(3);
        result.VisibleCategoryIds.Should().Contain(1).And.NotContain(2);
        result.Score.Questions.Single(q => q.QuestionId == 2).IsApplicable.Should().BeFalse();
    }

    [Fact]
    public void Required_zero_is_answered()
    {
        var result = _evaluator.Evaluate(Graph(Category(1, Question(1, AnswerType.Number, required: true))), Answers((1, new AnswerValue(null, 0m, null))), true);
        result.AnswerIssues.Should().BeEmpty();
    }

    [Fact]
    public void Hidden_required_question_does_not_block()
    {
        var graph = Graph(Category(1, Question(1, AnswerType.Dropdown, options: [10]), Question(2, AnswerType.Textbox, required: true, conditionId: 1)));
        graph.VisibilityConditions = [Condition(1, Predicate(1, ConditionOperator.HasAnyOption, 10))];

        var result = _evaluator.Evaluate(graph, Answers((1, new AnswerValue(null, null, []))), true);

        result.AnswerIssues.Should().BeEmpty();
        result.VisibleQuestionIds.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void Incomplete_save_does_not_raise_required_issue()
    {
        var result = _evaluator.Evaluate(Graph(Category(1, Question(1, AnswerType.Textbox, required: true))), new Dictionary<int, AnswerValue>(), requireComplete: false);
        result.AnswerIssues.Should().BeEmpty();
    }

    [Fact]
    public void Unanswered_optional_all_of_does_not_score()
    {
        var element = Question(1, AnswerType.Checkboxes, options: [10, 11]);
        var question = element.Question!;
        question.Rules.Add(new ScoringRule
        {
            Id = 1, Family = RuleFamily.Selection, MembershipOperator = MembershipOperator.AllOf, Points = 4,
            Options = [new ScoringRuleOption { QuestionOptionId = 10 }, new ScoringRuleOption { QuestionOptionId = 11 }]
        });
        var result = _evaluator.Evaluate(Graph(Category(1, element)), new Dictionary<int, AnswerValue>(), true);

        result.Score.Total.Should().Be(0);
        result.Score.Questions.Single().Points.Should().Be(0);
    }

    [Fact]
    public void Negative_points_and_invalid_enum_are_rejected()
    {
        var element = Question(1, (AnswerType)999);
        var question = element.Question!;
        question.Rules.Add(new ScoringRule { Id = 5, Family = RuleFamily.Presence, Points = -1 });

        var result = _evaluator.Evaluate(Graph(Category(1, element)), new Dictionary<int, AnswerValue>(), false);

        result.DefinitionIssues.Select(issue => issue.Code).Should().Contain(new[] { "invalid-answer-type", "invalid-points" });
    }

    [Fact]
    public void Static_only_category_remains_visible()
    {
        var result = _evaluator.Evaluate(Graph(Category(1, new Element { Id = 7, Kind = ElementKind.Content, Label = "Instructions" })), new Dictionary<int, AnswerValue>(), true);
        result.VisibleCategoryIds.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void Visibility_predicate_rejects_wrong_source_type_and_unknown_option()
    {
        var graph = Graph(Category(1, Question(1, AnswerType.Textbox), Question(2, AnswerType.Textbox, conditionId: 1)));
        graph.VisibilityConditions = [Condition(1, Predicate(1, ConditionOperator.HasAnyOption, 999))];

        var result = _evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), false);

        result.DefinitionIssues.Select(issue => issue.Code).Should().Contain("invalid-condition-operand");
    }

    [Fact]
    public void Visibility_predicate_rejects_forward_source_reference()
    {
        var graph = Graph(Category(1, Question(1, AnswerType.Textbox, conditionId: 1), Question(2, AnswerType.Textbox)));
        graph.VisibilityConditions = [Condition(1, new VisibilityPredicate { SourceQuestionId = 2, Operator = ConditionOperator.Answered })];

        var result = _evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), false);

        result.DefinitionIssues.Select(issue => issue.Code).Should().Contain("forward-condition-reference");
    }

    private static ApplicationVersionGraph Graph(params Category[] categories) => new()
    {
        Version = new ApplicationVersion { Id = 1, ApplicationId = 1, VersionNumber = 1 },
        Categories = categories
    };
    private static Category Category(int id, params Element[] elements) => new() { Id = id, ApplicationVersionId = 1, SortOrder = id, Name = $"Category {id}", Elements = elements.ToList() };
    private static Category CategoryWithCondition(int id, int conditionId, params Element[] elements) => new() { Id = id, ApplicationVersionId = 1, SortOrder = id, Name = $"Category {id}", VisibilityConditionId = conditionId, Elements = elements.ToList() };
    private static Element Question(int id, AnswerType type, bool required = false, int? conditionId = null, int[]? options = null)
    {
        var question = new Question { Id = id, ElementId = id, AnswerType = type, IsRequired = required };
        question.Options = (options ?? []).Select(optionId => new QuestionOption { Id = optionId, QuestionId = id }).ToList();
        return new Element { Id = id, CategoryId = 1, Kind = ElementKind.Question, Label = $"Question {id}", SortOrder = id, VisibilityConditionId = conditionId, Question = question };
    }
    private static VisibilityCondition Condition(int id, VisibilityPredicate predicate) => new() { Id = id, ApplicationVersionId = 1, Predicates = [predicate] };
    private static VisibilityPredicate Predicate(int sourceId, ConditionOperator op, int optionId) => new()
    {
        SourceQuestionId = sourceId, Operator = op, Options = [new VisibilityPredicateOption { QuestionOptionId = optionId }]
    };
    private static Dictionary<int, AnswerValue> Answers(params (int Id, AnswerValue Value)[] answers) => answers.ToDictionary(x => x.Id, x => x.Value);
}
