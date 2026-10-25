using FluentAssertions;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Authoring;

public sealed class DefinitionDependencyInspectorTests
{
    [Fact]
    public void Inspect_lists_direct_and_transitive_consumers_once_and_keeps_owned_configuration_separate()
    {
        var source = Question(11, "Annual income");
        var content = new Element { Id = 12, CategoryId = 1, Kind = ElementKind.Content, Label = "Income summary" };
        var category = new Category { Id = 1, Name = "Applicant", Elements = [source, content] };
        var first = new CalculatedField { Id = Guid.NewGuid(), Name = "Income subtotal", Sources = [QuestionSource(source)] };
        var second = new CalculatedField { Id = Guid.NewGuid(), Name = "Adjusted income", Sources = [CalculationSource(first.Id)] };
        var rule = new CompositionRule { Id = Guid.NewGuid(), Name = "Income eligibility", CategoryKey = category.LogicalKey,
            TargetQuestionKey = Guid.Empty, Kind = RulePurpose.Validation,
            Checks = [new ValueCheck { Left = CalculationSource(second.Id), Comparison = Comparison.GreaterThan, Right = Constant(0) }] };
        var variable = new DynamicVariable { Id = Guid.NewGuid(), Name = "Formatted income", Sources = [CalculationDynamicSource(second.Id)] };
        var chainedVariable = new DynamicVariable { Id = Guid.NewGuid(), Name = "Income display", Sources = [VariableDynamicSource(variable.Id)] };
        var template = new DynamicTemplate { Slot = DynamicTextSlot.ElementLabel, OwnerKey = content.LogicalKey,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value, Source = VariableDynamicSource(chainedVariable.Id) }] };
        var sourceHelpTemplate = new DynamicTemplate { Slot = DynamicTextSlot.ElementHelpText, OwnerKey = content.LogicalKey,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = source.LogicalKey } }] };
        var reviewTemplate = new DynamicTemplate { Slot = DynamicTextSlot.ReviewSummary,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = source.LogicalKey } }] };
        var ownTemplate = new DynamicTemplate { Slot = DynamicTextSlot.ElementHelpText, OwnerKey = source.LogicalKey,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Question-specific guidance" }] };
        var ownLabelTemplate = new DynamicTemplate { Slot = DynamicTextSlot.ElementLabel, OwnerKey = source.LogicalKey,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Annual income" }] };
        var graph = Graph([category], new CompositionDefinition
        {
            Calculations = [first, second], Rules = [rule],
            DynamicText = new DynamicTextDefinition { Variables = [variable, chainedVariable], Templates = [template, sourceHelpTemplate, reviewTemplate, ownTemplate, ownLabelTemplate] }
        });

        var report = DefinitionDependencyInspector.Inspect(graph, source.LogicalKey);

        report.Complete.Should().BeTrue();
        report.Items.Should().Contain(item => item.Name == "Income subtotal" && item.Relation == DependencyRelation.UsedBy && !item.IsIndirect);
        report.Items.Should().Contain(item => item.Name == "Adjusted income" && item.Relation == DependencyRelation.UsedBy && item.IsIndirect);
        report.Items.Should().Contain(item => item.Name == "Income eligibility" && item.Relation == DependencyRelation.UsedBy && item.IsIndirect);
        report.Items.Should().Contain(item => item.Name == "Formatted income" && item.Relation == DependencyRelation.UsedBy && item.IsIndirect);
        report.Items.Should().Contain(item => item.Name == "Income display" && item.Relation == DependencyRelation.UsedBy && item.IsIndirect);
        report.Items.Should().Contain(item => item.Name == "Income summary" && item.Relation == DependencyRelation.UsedBy && item.IsIndirect);
        report.Items.Should().Contain(i => i.Name == "Income summary" && i.Kind == "Dynamic help text reference" && i.Relation == DependencyRelation.UsedBy);
        report.Items.Should().Contain(i => i.Name == "Review summary" && i.Kind == "Dynamic text reference" && i.Relation == DependencyRelation.UsedBy);
        report.Items.Should().Contain(item => item.Name == "dynamic help text for Annual income" && item.Relation == DependencyRelation.Owned);
        report.Items.Should().Contain(item => item.Name == "dynamic label for Annual income" && item.Relation == DependencyRelation.Owned);
        report.Items.Count(item => item.Relation == DependencyRelation.Owned && item.Name.Contains("Annual income")).Should().Be(2);
        report.Items.Should().NotContain(item => item.Name == "Income subtotal" && item.Relation == DependencyRelation.Owned);
    }

    [Fact]
    public void Inspect_reports_visibility_groups_rule_targets_and_follow_up_relationships()
    {
        var option = new QuestionOption { Id = 21, QuestionId = 11, Label = "Yes", LogicalKey = Guid.NewGuid() };
        var source = Question(11, "Has income"); source.Question!.Options = [option];
        var content = new Element { Id = 12, CategoryId = 1, Kind = ElementKind.Content, Label = "Income details", VisibilityConditionId = 5 };
        var category = new Category { Id = 1, Name = "Applicant", VisibilityConditionId = 5, Elements = [source, content] };
        var child = Question(13, "Income type"); child.ParentOptionId = option.Id;
        category.Elements.Add(child);
        var sameNamedCategory = new Category { Id = 2, Name = "Applicant", VisibilityConditionId = 5 };
        source.Question.Rules = [new ScoringRule { Id = 41 }];
        var targetedRule = new CompositionRule { Id = Guid.NewGuid(), Name = "Income check", CategoryKey = category.LogicalKey,
            TargetQuestionKey = source.LogicalKey, Kind = RulePurpose.Validation, Checks = [new ValueCheck
            { Left = new ValueSource { Kind = SourceKind.OptionSelected, Key = option.LogicalKey }, Right = Constant(1) }] };
        var defaultRule = new CompositionRule { Id = Guid.NewGuid(), Name = "First visible check", CategoryKey = category.LogicalKey,
            TargetQuestionKey = Guid.Empty, Kind = RulePurpose.Score, Checks = [new ValueCheck { Left = QuestionSource(source), Right = Constant(1) }] };
        var graph = Graph([category, sameNamedCategory], new CompositionDefinition { Groups = [new QuestionGroup { Name = "Applicant data", Rows = [new GroupRow { QuestionKey = source.LogicalKey }] }], Rules = [targetedRule, defaultRule] });
        graph.VisibilityConditions = [new VisibilityCondition { Id = 5, Predicates = [new VisibilityPredicate { SourceQuestionId = source.Question.Id, Options = [new VisibilityPredicateOption { QuestionOptionId = option.Id }] }] }];

        var report = DefinitionDependencyInspector.Inspect(graph, source.LogicalKey);

        report.Items.Should().Contain(i => i.Kind == "Question group" && i.Name == "Applicant data");
        report.Items.Should().Contain(i => i.Kind == "Category visibility" && i.CategoryId == category.Id);
        report.Items.Should().Contain(i => i.Kind == "Category visibility" && i.CategoryId == sameNamedCategory.Id);
        report.Items.Should().Contain(i => i.Kind == "Content visibility" && i.ElementId == content.Id);
        report.Items.Should().Contain(i => i.Relation == DependencyRelation.UsedBy && i.Kind == "Follow-up question" && i.ElementId == child.Id);
        report.Items.Should().Contain(i => i.Relation == DependencyRelation.UsedBy && i.Name == "Income check");
        report.Items.Should().Contain(i => i.Relation == DependencyRelation.PotentialTarget && i.Name == "First visible check");
        report.Items.Should().Contain(i => i.Relation == DependencyRelation.UsedBy && i.Name == "First visible check");
        report.Items.Should().ContainSingle(i => i.Relation == DependencyRelation.Owned && i.Kind == "Question scoring");

        var childReport = DefinitionDependencyInspector.Inspect(graph, child.LogicalKey);
        childReport.Items.Should().Contain(i => i.Relation == DependencyRelation.DependsOn && i.Kind == "Follow-up parent" && i.ElementId == source.Id);
    }

    [Fact]
    public void Inspect_terminates_cycles_and_marks_malformed_or_duplicate_composition_incomplete()
    {
        var source = Question(11, "Annual income");
        var category = new Category { Id = 1, Name = "Applicant", Elements = [source] };
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var calcA = new CalculatedField { Id = firstId, Name = "A", Sources = [QuestionSource(source), null!, CalculationSource(secondId)] };
        var calcB = new CalculatedField { Id = secondId, Name = "B", Sources = [CalculationSource(firstId)] };
        var variableAId = Guid.NewGuid(); var variableBId = Guid.NewGuid();
        var variableA = new DynamicVariable { Id = variableAId, Name = "Variable A", Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = source.LogicalKey }, null!, new DynamicSource { Kind = DynamicSourceKind.Variable, Key = variableBId }] };
        var variableB = new DynamicVariable { Id = variableBId, Name = "Variable B", Sources = [new DynamicSource { Kind = DynamicSourceKind.Variable, Key = variableAId }] };
        var definition = new CompositionDefinition { Calculations = [calcA, calcB, new CalculatedField { Id = firstId, Name = "Duplicate" }],
            DynamicText = new DynamicTextDefinition { Variables = [variableA, variableB, null!], Templates = [new DynamicTemplate { OwnerKey = source.LogicalKey, Slot = DynamicTextSlot.ElementLabel, Parts = null! }] } };

        var report = DefinitionDependencyInspector.Inspect(Graph([category], definition), source.LogicalKey);

        report.Complete.Should().BeFalse();
        report.Warnings.Should().Contain(w => w.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        report.Warnings.Should().Contain(w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        report.Warnings.Should().Contain(w => w.Contains("template parts", StringComparison.OrdinalIgnoreCase));
        report.Warnings.Should().Contain(w => w.Contains("calculation sources", StringComparison.OrdinalIgnoreCase));
        report.Warnings.Should().Contain(w => w.Contains("variable sources", StringComparison.OrdinalIgnoreCase));
        report.Items.Should().Contain(i => i.Name == "A" && i.Relation == DependencyRelation.UsedBy);
        report.Items.Should().Contain(i => i.Name == "Variable A" && i.Relation == DependencyRelation.UsedBy);

        var malformedGraph = Graph([category], new CompositionDefinition());
        malformedGraph.Version.CompositionJson = "{broken";
        var malformedJson = DefinitionDependencyInspector.Inspect(malformedGraph, source.LogicalKey);
        malformedJson.Complete.Should().BeFalse();
        malformedJson.Warnings.Should().Contain(w => w.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    private static Element Question(int id, string label) => new()
    {
        Id = id, CategoryId = 1, Kind = ElementKind.Question, Label = label,
        Question = new Question { Id = id, AnswerType = AnswerType.Number }
    };
    private static ApplicationVersionGraph Graph(IReadOnlyList<Category> categories, CompositionDefinition definition) => new()
    {
        Version = new ApplicationVersion { ApplicationId = 4, CompositionJson = definition.ToJson() },
        Categories = categories
    };
    private static ValueSource QuestionSource(Element element) => new() { Kind = SourceKind.Question, Key = element.LogicalKey };
    private static ValueSource CalculationSource(Guid key) => new() { Kind = SourceKind.Calculation, Key = key };
    private static ValueSource Constant(decimal value) => new() { Kind = SourceKind.Constant, Value = value };
    private static DynamicSource CalculationDynamicSource(Guid key) => new() { Kind = DynamicSourceKind.Calculation, Key = key };
    private static DynamicSource VariableDynamicSource(Guid key) => new() { Kind = DynamicSourceKind.Variable, Key = key };
}
