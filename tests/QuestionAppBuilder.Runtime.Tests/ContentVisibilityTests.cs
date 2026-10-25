using FluentAssertions;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Core.Rendering;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class ContentVisibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Evaluator_and_renderer_follow_content_visibility_condition(bool answered, bool expectedVisible)
    {
        var source = new Element
        {
            Id = 1,
            Kind = ElementKind.Question,
            Label = "Has details?",
            Question = new Question { Id = 1, ElementId = 1, AnswerType = AnswerType.Textbox }
        };
        var content = new Element
        {
            Id = 2,
            Kind = ElementKind.Content,
            Label = "Conditional instructions",
            SortOrder = 2,
            VisibilityConditionId = 1
        };
        var dynamicText = new CompositionDefinition();
        dynamicText.DynamicText.Templates.Add(new DynamicTemplate { Slot = DynamicTextSlot.ElementHelpText,
            OwnerKey = content.LogicalKey,
            Parts = { new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Dynamic content help" } } });
        var condition = new VisibilityCondition
        {
            Id = 1,
            ApplicationVersionId = 1,
            Predicates = [new VisibilityPredicate { SourceQuestionId = 1, Operator = ConditionOperator.Answered }]
        };
        var category = new Category { Id = 1, Name = "Details", Elements = [source, content] };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 1, ApplicationId = 1, VersionNumber = 1, CompositionJson = dynamicText.ToJson() },
            Categories = [category],
            VisibilityConditions = [condition]
        };

        var answers = answered
            ? new Dictionary<int, AnswerValue> { [1] = new("yes", null, null) }
            : new Dictionary<int, AnswerValue>();
        var evaluation = new DefinitionEvaluator().Evaluate(graph, answers, requireComplete: false);
        var rendered = new QuestionnaireRenderModelFactory().Create(graph, answers, evaluation, preview: false);

        rendered.Categories.Should().ContainSingle();
        evaluation.VisibleElementIds.Should().Contain(1);
        evaluation.VisibleElementIds!.Contains(2).Should().Be(expectedVisible);
        evaluation.Composition!.DynamicText!.Texts.Any(text => text.OwnerKey == content.LogicalKey).Should().Be(expectedVisible);
        rendered.Categories[0].Elements.Select(element => element.Label).Contains("Conditional instructions").Should().Be(expectedVisible);
    }

    [Fact]
    public void Renderer_distinguishes_missing_element_visibility_from_an_explicit_empty_set()
    {
        var content = new Element { Id = 2, Kind = ElementKind.Content, Label = "Unconditional instructions" };
        var category = new Category { Id = 1, Name = "Details", Elements = [content] };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 1, ApplicationId = 1, VersionNumber = 1 },
            Categories = [category]
        };
        var legacy = new EvaluationResult(new HashSet<int> { 1 }, new HashSet<int>(), [], [],
            new SubmissionScore(0, [], []));

        new QuestionnaireRenderModelFactory().Create(graph, new Dictionary<int, AnswerValue>(), legacy, false)
            .Categories.Single().Elements.Should().ContainSingle(element => element.Label == "Unconditional instructions");
        new QuestionnaireRenderModelFactory().Create(graph, new Dictionary<int, AnswerValue>(), legacy with
            { VisibleElementIds = new HashSet<int>() }, false).Categories.Single().Elements.Should().BeEmpty();
    }
}
