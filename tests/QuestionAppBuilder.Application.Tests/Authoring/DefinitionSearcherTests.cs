using FluentAssertions;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Authoring;

public sealed class DefinitionSearcherTests
{
    [Fact]
    public void Searches_question_content_option_value_message_and_follow_up_parent_as_own_or_direct_reference()
    {
        var graph = Graph();
        var amount = QuestionResult(20);
        amount.Question!.RequiredMessage = "Enter a funding amount for review.";
        var option = new QuestionOption { Id = 30, LogicalKey = Guid.NewGuid(), QuestionId = 20, Label = "Other", Value = "other-value" };
        amount.Question.Options.Add(option);
        var followUp = QuestionResult(21, "Describe the other source");
        followUp.ParentOptionId = option.Id;
        var content = new Element { Id = 12, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Content, Label = "Applicant instructions", HelpText = "Attach a complete overview." };
        var category = graph.Categories[0];
        category.Elements = [amount, followUp, content];

        var byValue = DefinitionSearcher.Search(graph, "OTHER-VALUE").Items.Single(x => x.Kind == DefinitionSearchItemKind.Option);
        byValue.MatchKind.Should().Be(DefinitionSearchMatchKind.Own);
        byValue.MatchedText.Should().Be("other-value");
        byValue.Destination.Fragment.Should().Be("option-30");

        var byMessage = DefinitionSearcher.Search(graph, "funding amount").Items.Single(x => x.Name == "Requested amount");
        byMessage.MatchKind.Should().Be(DefinitionSearchMatchKind.Own);
        byMessage.MatchedText.Should().Contain("funding amount");

        var byParent = DefinitionSearcher.Search(graph, "Parent question").Items.Single(x => x.Name == "Describe the other source");
        byParent.MatchKind.Should().Be(DefinitionSearchMatchKind.Reference);
        byParent.ReferenceLabel.Should().Contain("Requested amount").And.Contain("Other");

        DefinitionSearcher.Search(graph, "complete overview").Items.Single().Kind.Should().Be(DefinitionSearchItemKind.Content);
    }

    [Fact]
    public void Searches_composition_objects_by_own_text_and_direct_resolved_references_only()
    {
        var graph = Graph();
        var question = QuestionResult(20);
        graph.Categories[0].Elements = [question, QuestionResult(21, "Decision detail")];
        var directCalculation = new CalculatedField
        {
            Id = Guid.NewGuid(), Name = "Direct amount total", CategoryKey = graph.Categories[0].LogicalKey,
            Sources = [new ValueSource { Kind = SourceKind.Question, Key = question.LogicalKey }]
        };
        var indirectCalculation = new CalculatedField
        {
            Id = Guid.NewGuid(), Name = "Indirect amount result", CategoryKey = graph.Categories[0].LogicalKey,
            Sources = [new ValueSource { Kind = SourceKind.Calculation, Key = directCalculation.Id }]
        };
        var rule = new CompositionRule
        {
            Id = Guid.NewGuid(), Name = "Approve total", CategoryKey = graph.Categories[0].LogicalKey,
            TargetQuestionKey = graph.Categories[0].Elements[1].LogicalKey, Message = "Review the direct amount total.",
            Conditions = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Calculation, Key = indirectCalculation.Id }, Right = new ValueSource { Kind = SourceKind.Constant, Value = 1 } }]
        };
        var variable = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Funding label", CategoryKey = graph.Categories[0].LogicalKey,
            Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = question.LogicalKey }]
        };
        var definition = new CompositionDefinition
        {
            Groups = [new QuestionGroup { Id = Guid.NewGuid(), Name = "Funding table", CategoryKey = graph.Categories[0].LogicalKey,
                Rows = [new GroupRow { QuestionKey = question.LogicalKey, Cells = ["Requested input"] }] }],
            Calculations = [directCalculation, indirectCalculation], Rules = [rule],
            DynamicText = new DynamicTextDefinition { Variables = [variable], Templates = [new DynamicTemplate
            {
                Slot = DynamicTextSlot.ReviewSummary, OwnerKey = Guid.Empty,
                Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = variable.Id } }]
            }] }
        };
        graph.Version.CompositionJson = definition.ToJson();

        var directMatches = DefinitionSearcher.Search(graph, "Requested amount").Items;
        directMatches.Should().Contain(x => x.Kind == DefinitionSearchItemKind.Calculation && x.Name == directCalculation.Name && x.MatchKind == DefinitionSearchMatchKind.Reference);
        directMatches.Should().Contain(x => x.Kind == DefinitionSearchItemKind.Group && x.MatchKind == DefinitionSearchMatchKind.Reference);
        directMatches.Should().Contain(x => x.Kind == DefinitionSearchItemKind.DynamicVariable && x.MatchKind == DefinitionSearchMatchKind.Reference);
        directMatches.Should().NotContain(x => x.Kind == DefinitionSearchItemKind.Calculation && x.Name == indirectCalculation.Name,
            "references are direct and do not follow a calculation transitively");
        directMatches.Should().NotContain(x => x.Kind == DefinitionSearchItemKind.CombinedRule,
            "the rule refers to the indirect calculation rather than directly to the question");
        directMatches.Should().NotContain(x => x.Kind == DefinitionSearchItemKind.DynamicTemplate,
            "the template refers to a variable, not transitively to that variable's question source");

        var variableMatches = DefinitionSearcher.Search(graph, "Funding label").Items;
        variableMatches.Should().Contain(x => x.Kind == DefinitionSearchItemKind.DynamicTemplate && x.MatchKind == DefinitionSearchMatchKind.Reference);
        variableMatches.Should().NotContain(x => x.Kind == DefinitionSearchItemKind.CombinedRule);
        DefinitionSearcher.Search(graph, "Decision detail").Items.Should().Contain(x => x.Kind == DefinitionSearchItemKind.CombinedRule && x.MatchKind == DefinitionSearchMatchKind.Reference);
        DefinitionSearcher.Search(graph, "Indirect amount result").Items.Should().Contain(x => x.Kind == DefinitionSearchItemKind.CombinedRule && x.MatchKind == DefinitionSearchMatchKind.Reference);
        DefinitionSearcher.Search(graph, "Review the direct amount").Items.Single().Kind.Should().Be(DefinitionSearchItemKind.CombinedRule);
    }

    [Fact]
    public void Visibility_is_one_entry_per_condition_including_unassigned_conditions_and_direct_targets()
    {
        var graph = Graph();
        var source = QuestionResult(20);
        var option = new QuestionOption { Id = 30, LogicalKey = Guid.NewGuid(), QuestionId = source.Question!.Id, Label = "Approved", Value = "yes" };
        source.Question.Options.Add(option);
        var firstTarget = QuestionResult(21, "First target");
        var secondTarget = QuestionResult(22, "Second target");
        var condition = new VisibilityCondition
        {
            Id = 90, ApplicationVersionId = graph.Version.Id, Junction = ConditionJunction.All,
            Predicates = [new VisibilityPredicate { Id = 1, SourceQuestionId = source.Question.Id, Operator = ConditionOperator.HasAnyOption,
                Options = [new VisibilityPredicateOption { QuestionOptionId = option.Id }] }]
        };
        firstTarget.VisibilityConditionId = condition.Id;
        secondTarget.VisibilityConditionId = condition.Id;
        graph.Categories[0].Elements = [source, firstTarget, secondTarget];
        graph.VisibilityConditions = [condition, new VisibilityCondition { Id = 91, Junction = ConditionJunction.Any, Predicates = [] }];

        var results = DefinitionSearcher.Search(graph, "First target").Items.Where(x => x.Kind == DefinitionSearchItemKind.Visibility).ToArray();
        results.Should().ContainSingle();
        results[0].ReferenceLabel.Should().Contain("First target");
        results[0].Context.Should().Contain("First target").And.Contain("Second target");
        results[0].Destination.Fragment.Should().Be("condition-90");
        results[0].MatchKind.Should().Be(DefinitionSearchMatchKind.Reference);

        var unassigned = DefinitionSearcher.Search(graph, "Visibility condition").Items.Single(x => x.Kind == DefinitionSearchItemKind.Visibility && x.Destination.Fragment == "condition-91");
        unassigned.Context.Should().Be("Unassigned");
        unassigned.Destination.Fragment.Should().Be("condition-91");
    }

    [Fact]
    public void Default_combined_rule_target_and_dynamic_template_literals_are_valid_own_text()
    {
        var graph = Graph();
        graph.Categories[0].Elements = [QuestionResult(20)];
        var rule = new CompositionRule
        {
            Id = Guid.NewGuid(), Name = "Default target rule", CategoryKey = graph.Categories[0].LogicalKey,
            TargetQuestionKey = Guid.Empty, Message = "Default target sentinel message", Conditions = [], Checks = []
        };
        var template = new DynamicTemplate
        {
            Slot = DynamicTextSlot.ReviewSummary, OwnerKey = Guid.Empty,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value,
                Source = new DynamicSource { Kind = DynamicSourceKind.Text, Text = "Literal template marker" } }]
        };
        var variable = new DynamicVariable { Id = Guid.NewGuid(), Name = "Literal number", Sources = [new DynamicSource { Kind = DynamicSourceKind.Number, Number = 7 }] };
        graph.Version.CompositionJson = new CompositionDefinition
        {
            Rules = [rule], DynamicText = new DynamicTextDefinition { Variables = [variable], Templates = [template] }
        }.ToJson();

        var ruleResult = DefinitionSearcher.Search(graph, "Default target sentinel").Items.Single();
        ruleResult.Kind.Should().Be(DefinitionSearchItemKind.CombinedRule);
        ruleResult.MatchKind.Should().Be(DefinitionSearchMatchKind.Own);
        var templateResult = DefinitionSearcher.Search(graph, "Literal template marker").Items.Single();
        templateResult.Kind.Should().Be(DefinitionSearchItemKind.DynamicTemplate);
        templateResult.MatchKind.Should().Be(DefinitionSearchMatchKind.Own);
        templateResult.ReferenceLabel.Should().BeNull();
        DefinitionSearcher.Search(graph, "7").Items.Single().Kind.Should().Be(DefinitionSearchItemKind.DynamicVariable);
        DefinitionSearcher.Search(graph, "00000000-0000-0000-0000-000000000000").Items.Should().BeEmpty();
    }

    [Fact]
    public void Ambiguous_duplicate_references_are_not_resolved_to_an_arbitrary_item()
    {
        var graph = Graph();
        var duplicateId = Guid.NewGuid();
        var rule = new CompositionRule
        {
            Id = Guid.NewGuid(), Name = "Rule with ambiguous calculation", CategoryKey = graph.Categories[0].LogicalKey,
            Conditions = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Calculation, Key = duplicateId }, Right = new ValueSource { Kind = SourceKind.Constant, Value = 2 } }]
        };
        graph.Version.CompositionJson = new CompositionDefinition
        {
            Calculations =
            [
                new CalculatedField { Id = duplicateId, Name = "Duplicate amber calculation", CategoryKey = graph.Categories[0].LogicalKey },
                new CalculatedField { Id = duplicateId, Name = "Duplicate blue calculation", CategoryKey = graph.Categories[0].LogicalKey },
                new CalculatedField { Id = duplicateId, Name = "Duplicate third calculation", CategoryKey = graph.Categories[0].LogicalKey }
            ],
            Rules = [rule]
        }.ToJson();

        var report = DefinitionSearcher.Search(graph, "Duplicate amber");
        report.Items.Should().BeEmpty();
        report.Complete.Should().BeFalse();
        report.Warnings.Should().NotBeEmpty();
        DefinitionSearcher.Search(graph, "Duplicate third calculation").Items.Should().BeEmpty(
            "a third duplicate key must remain ambiguous instead of re-resolving to the last item");
    }

    [Fact]
    public void Malformed_composition_preserves_base_hits_and_reports_incomplete_results()
    {
        var graph = Graph();
        graph.Categories[0].Elements = [new Element { Id = 10, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Content,
            Label = "Existing content label", HelpText = "Useful search phrase" }];
        graph.Version.CompositionJson = "{ malformed";

        var report = DefinitionSearcher.Search(graph, "Useful search");

        report.Items.Should().ContainSingle(x => x.Kind == DefinitionSearchItemKind.Content);
        report.Complete.Should().BeFalse();
        report.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Long_text_match_excerpt_is_centered_on_the_search_text()
    {
        var longText = new string('x', 600) + "needle-at-the-end-of-a-long-field" + new string('z', 600);
        var graph = Graph();
        graph.Categories[0].Elements = [new Element { Id = 10, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Content,
            Label = "Long help text", HelpText = longText }];

        var result = DefinitionSearcher.Search(graph, "needle-at-the-end").Items.Single();

        result.MatchedText.Should().Contain("needle-at-the-end");
        result.MatchedText.Length.Should().BeLessThanOrEqualTo(240);
        result.MatchedText.Should().StartWith("…").And.EndWith("…");
    }

    [Fact]
    public void Ranks_exact_then_prefix_then_own_text_then_reference_and_caps_stably()
    {
        var exact = new Category { Id = 1, LogicalKey = Guid.NewGuid(), Name = "needle" };
        var prefix = new Category { Id = 2, LogicalKey = Guid.NewGuid(), Name = "needle prefix" };
        var ownText = new Category { Id = 3, LogicalKey = Guid.NewGuid(), Name = "Text owner", Elements =
            [new Element { Id = 30, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Content, Label = "Content", HelpText = "contains needle inside" }] };
        var referenceOwner = new Category { Id = 4, LogicalKey = Guid.NewGuid(), Name = "Reference owner", Elements =
            [new Element { Id = 40, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Content, Label = "Child item" }] };
        var graph = new ApplicationVersionGraph { Version = new ApplicationVersion { Id = 17 }, Categories = [exact, prefix, ownText, referenceOwner] };
        // Add a category as a resolved direct reference through a group.
        graph.Version.CompositionJson = new CompositionDefinition { Groups = [new QuestionGroup
        { Id = Guid.NewGuid(), Name = "Group", CategoryKey = exact.LogicalKey }] }.ToJson();

        var ranked = DefinitionSearcher.Search(graph, "needle").Items;
        ranked[0].Kind.Should().Be(DefinitionSearchItemKind.Category);
        ranked[0].Name.Should().Be("needle");
        ranked[1].Name.Should().Be("needle prefix");
        ranked[2].MatchKind.Should().Be(DefinitionSearchMatchKind.Own);
        ranked[3].MatchKind.Should().Be(DefinitionSearchMatchKind.Reference);

        var many = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 17, CompositionJson = new CompositionDefinition().ToJson() },
            Categories = Enumerable.Range(1, 60).Select(i => new Category
            { Id = i, LogicalKey = Guid.NewGuid(), Name = $"needle {i:00}" }).ToArray()
        };
        var capped = DefinitionSearcher.Search(many, "needle");
        capped.TotalCount.Should().Be(60);
        capped.Items.Should().HaveCount(DefinitionSearcher.MaximumResults);
        capped.Items.First().Name.Should().Be("needle 01");
        capped.Items.Last().Name.Should().Be("needle 50");
        capped.Complete.Should().BeFalse();
        capped.Warnings.Should().Contain(x => x.Contains("limited to", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_search_text(string query)
        => FluentActions.Invoking(() => DefinitionSearcher.Search(Graph(), query)).Should().Throw<ArgumentException>();

    [Fact]
    public void Rejects_search_text_longer_than_two_hundred_characters()
        => FluentActions.Invoking(() => DefinitionSearcher.Search(Graph(), new string('x', 201))).Should().Throw<ArgumentException>();

    private static ApplicationVersionGraph Graph()
    {
        var category = new Category { Id = 1, LogicalKey = Guid.NewGuid(), Name = "Funding" };
        return new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 17, CompositionJson = new CompositionDefinition().ToJson() },
            Categories = [category]
        };
    }

    private static Element QuestionResult(int questionId, string label = "Requested amount") => new()
    {
        Id = questionId - 10, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Question, Label = label,
        Question = new Question { Id = questionId, AnswerType = AnswerType.Number, Options = [], Rules = [] }
    };
}
