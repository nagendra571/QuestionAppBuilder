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
    public void Invalid_follow_up_ownership_is_reported_without_revealing_or_recursing_forever()
    {
        var parent = Question(1, AnswerType.RadioGroup, options: [10]);
        var child = Question(2, AnswerType.RadioGroup, options: [20]);
        parent.ParentOptionId = 20;
        child.ParentOptionId = 10;
        var cyclic = _evaluator.Evaluate(Graph(Category(1, parent, child)),
            Answers((1, new AnswerValue(null, null, [10])), (2, new AnswerValue(null, null, [20]))), true);
        cyclic.DefinitionIssues.Select(i => i.Code).Should().Contain("invalid-follow-up");
        cyclic.VisibleQuestionIds.Should().BeEmpty();
        parent.ParentOptionId = null;
        var crossCategory = _evaluator.Evaluate(Graph(Category(1, parent), Category(2, child)), new Dictionary<int, AnswerValue>(), false);
        crossCategory.DefinitionIssues.Select(i => i.Code).Should().Contain("invalid-follow-up");
    }

    [Fact]
    public void Follow_up_branches_require_selected_visible_ancestors_and_ignore_stale_answers()
    {
        var parent = Question(1, AnswerType.Checkboxes, options: [10, 11]);
        var child = Question(2, AnswerType.RadioGroup, required: true, options: [20]);
        child.ParentOptionId = 10;
        var nested = Question(3, AnswerType.Textbox, required: true);
        nested.ParentOptionId = 20;
        child.VisibilityConditionId = 1;
        child.Question!.Rules.Add(new ScoringRule { Id = 10, Family = RuleFamily.Presence, Points = 7 });
        var graph = Graph(Category(1, parent, child, nested));
        graph.VisibilityConditions = [Condition(1, Predicate(1, ConditionOperator.HasAnyOption, 11))];
        var answers = Answers((1, new AnswerValue(null, null, [11])), (2, new AnswerValue(null, null, [20])));
        var hidden = _evaluator.Evaluate(graph, answers, true);
        hidden.VisibleQuestionIds.Should().BeEquivalentTo([1]);
        hidden.AnswerIssues.Should().BeEmpty();
        hidden.Score.Total.Should().Be(0);
        answers = Answers((1, new AnswerValue(null, null, [10, 11])), (2, new AnswerValue(null, null, [20])));
        var shown = _evaluator.Evaluate(graph, answers, true);
        shown.VisibleQuestionIds.Should().BeEquivalentTo([1, 2, 3]);
        shown.Score.Total.Should().Be(7);
        shown.AnswerIssues.Should().ContainSingle().Which.QuestionId.Should().Be(3);
    }

    [Theory]
    [InlineData("Please enter your organization name.", "Please enter your organization name.")]
    [InlineData(null, "A response is required.")]
    [InlineData("   ", "A response is required.")]
    public void Missing_required_answer_uses_question_message_or_default(string? configured, string expected)
    {
        var element = Question(1, AnswerType.Textbox, required: true);
        element.Question!.RequiredMessage = configured;
        var graph = Graph(Category(1, element));
        var result = _evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), true);
        result.AnswerIssues.Should().ContainSingle().Which.Message.Should().Be(expected);
        _evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), false).AnswerIssues.Should().BeEmpty();
        element.Question!.IsRequired = false;
        _evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), true).AnswerIssues.Should().BeEmpty();
    }

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

    public static TheoryData<decimal> InvalidWholeNumberValues => new() { -1m, 1.5m };

    [Theory]
    [MemberData(nameof(InvalidWholeNumberValues))]
    public void Invalid_whole_number_per_unit_answers_are_reported_and_not_scored(decimal value)
    {
        var element = Question(1, AnswerType.Number);
        var question = element.Question!;
        question.WholeNumbersOnly = true;
        question.MinValue = 0;
        question.MaxValue = 100;
        question.Rules.Add(new ScoringRule
        {
            Id = 1, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 2, MaximumPoints = 200, MinValue = 0, MaxValue = 100
        });

        var result = _evaluator.Evaluate(Graph(Category(1, element)), Answers((1, new AnswerValue(null, value, null))), true);

        result.AnswerIssues.Select(issue => issue.Code).Should().Contain("invalid-answer");
        result.Score.Total.Should().Be(0);
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
