using FluentAssertions;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Authoring;

public sealed class DraftDeletionImpactAnalyzerTests
{
    [Fact]
    public void Element_preview_separates_cascade_and_automatic_changes_from_retained_references_to_repair()
    {
        var source = Question(11, 1, "Income");
        var branchRoot = Question(12, 1, "Follow-up");
        var branchChild = Question(13, 1, "Nested follow-up");
        var choice = new QuestionOption { Id = 31, QuestionId = 11, Label = "More", Value = "more", LogicalKey = Guid.NewGuid() };
        var nestedChoice = new QuestionOption { Id = 32, QuestionId = 12, Label = "Nested", Value = "nested", LogicalKey = Guid.NewGuid() };
        source.Question!.Options = [choice];
        branchRoot.ParentOptionId = choice.Id;
        branchRoot.Question!.Options = [nestedChoice];
        branchChild.ParentOptionId = nestedChoice.Id;
        var target = Question(21, 2, "Other question");
        var targetCategory = new Category { Id = 2, Name = "Keep", LogicalKey = Guid.NewGuid(), Elements = [target] };
        var sourceCategory = new Category { Id = 1, Name = "Remove", LogicalKey = Guid.NewGuid(), Elements = [source, branchRoot, branchChild] };
        var condition = new VisibilityCondition { Id = 7, Predicates = [new VisibilityPredicate { SourceQuestionId = source.Question!.Id, Operator = ConditionOperator.Answered }] };
        target.VisibilityConditionId = condition.Id;
        var retainedCalculation = new CalculatedField { Id = Guid.NewGuid(), Name = "Other total", CategoryKey = targetCategory.LogicalKey,
            Sources = [QuestionSource(source)] };
        var dependentCalculation = new CalculatedField { Id = Guid.NewGuid(), Name = "Other adjusted total", CategoryKey = targetCategory.LogicalKey,
            Sources = [CalculationSource(retainedCalculation.Id)] };
        var unrelatedCalculation = new CalculatedField { Id = Guid.NewGuid(), Name = "Unrelated total", CategoryKey = targetCategory.LogicalKey,
            Sources = [Constant(5)] };
        var retainedVariable = new DynamicVariable { Id = Guid.NewGuid(), Name = "Other label", CategoryKey = targetCategory.LogicalKey,
            Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = source.LogicalKey }] };
        var dependentVariable = new DynamicVariable { Id = Guid.NewGuid(), Name = "Other formatted label", CategoryKey = targetCategory.LogicalKey,
            Sources = [new DynamicSource { Kind = DynamicSourceKind.Variable, Key = retainedVariable.Id }] };
        var unrelatedVariable = new DynamicVariable { Id = Guid.NewGuid(), Name = "Unrelated display", CategoryKey = targetCategory.LogicalKey,
            Sources = [new DynamicSource { Kind = DynamicSourceKind.Number, Number = 5 }] };
        var retainedTemplate = new DynamicTemplate { Slot = DynamicTextSlot.ElementHelpText, OwnerKey = target.LogicalKey,
            Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = dependentVariable.Id } }] };
        var retainedRule = new CompositionRule { Id = Guid.NewGuid(), Name = "Other review", CategoryKey = targetCategory.LogicalKey,
            Kind = RulePurpose.Validation, TargetQuestionKey = target.LogicalKey,
            Checks = [new ValueCheck { Left = CalculationSource(dependentCalculation.Id), Comparison = Comparison.GreaterThan, Right = Constant(0) }] };
        var graph = Graph([sourceCategory, targetCategory], new CompositionDefinition
        {
            Groups = [new QuestionGroup { Name = "Mixed group", CategoryKey = targetCategory.LogicalKey,
                Rows = [new GroupRow { QuestionKey = source.LogicalKey }, new GroupRow { QuestionKey = target.LogicalKey }] },
                new QuestionGroup { Name = "Preexisting empty group", CategoryKey = targetCategory.LogicalKey, Rows = [] }],
            Calculations = [retainedCalculation, dependentCalculation, unrelatedCalculation], Rules = [retainedRule],
            DynamicText = new DynamicTextDefinition { Variables = [retainedVariable, dependentVariable, unrelatedVariable], Templates = [retainedTemplate] }
        }, [condition]);

        var preview = DraftDeletionImpactAnalyzer.Analyze(graph, DraftDeletionKind.Element, source.Id);

        preview.CanDelete.Should().BeTrue();
        preview.Removed.Should().Contain(item => item.Name == "Income");
        preview.Removed.Should().Contain(item => item.Name == "Follow-up");
        preview.Removed.Should().Contain(item => item.Name == "Nested follow-up");
        preview.AutomaticallyChanged.Should().Contain(item => item.Name.Contains("Mixed group", StringComparison.Ordinal));
        preview.Removed.Should().Contain(item => item.Name == "Preexisting empty group");
        preview.AutomaticallyChanged.Should().Contain(item => item.Detail.Contains("visibility", StringComparison.OrdinalIgnoreCase));
        preview.NeedsRepair.Should().Contain(item => item.Name == "Other total");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Other adjusted total");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Other label");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Other formatted label");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Other review");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Other question");
        preview.NeedsRepair.Should().NotContain(item => item.Name == "Unrelated total" || item.Name == "Unrelated display");
        preview.Blocked.Should().BeEmpty();
    }

    [Fact]
    public void Category_preview_removes_owned_configuration_but_reports_surviving_cross_category_references()
    {
        var source = Question(11, 1, "Amount");
        var sourceCategory = new Category { Id = 1, Name = "Budget", LogicalKey = Guid.NewGuid(), Elements = [source] };
        var target = Question(21, 2, "Reviewer note");
        var targetCategory = new Category { Id = 2, Name = "Review", LogicalKey = Guid.NewGuid(), Elements = [target] };
        var ownedCalculation = new CalculatedField { Id = Guid.NewGuid(), Name = "Budget total", CategoryKey = sourceCategory.LogicalKey,
            Sources = [QuestionSource(source)] };
        var retainedCalculation = new CalculatedField { Id = Guid.NewGuid(), Name = "Review total", CategoryKey = targetCategory.LogicalKey,
            Sources = [CalculationSource(ownedCalculation.Id)] };
        var ownedRule = new CompositionRule { Id = Guid.NewGuid(), Name = "Budget validation", CategoryKey = sourceCategory.LogicalKey,
            Kind = RulePurpose.Validation, Checks = [new ValueCheck { Left = CalculationSource(ownedCalculation.Id), Right = Constant(1) }] };
        var retainedRule = new CompositionRule { Id = Guid.NewGuid(), Name = "Review validation", CategoryKey = targetCategory.LogicalKey,
            Kind = RulePurpose.Validation, TargetQuestionKey = target.LogicalKey,
            Checks = [new ValueCheck { Left = CalculationSource(ownedCalculation.Id), Right = Constant(1) }] };
        var graph = Graph([sourceCategory, targetCategory], new CompositionDefinition
        {
            Groups = [new QuestionGroup { Name = "Budget group", CategoryKey = sourceCategory.LogicalKey, Rows = [new GroupRow { QuestionKey = source.LogicalKey }] }],
            Calculations = [ownedCalculation, retainedCalculation], Rules = [ownedRule, retainedRule]
        });

        var preview = DraftDeletionImpactAnalyzer.Analyze(graph, DraftDeletionKind.Category, sourceCategory.Id);

        preview.Removed.Should().Contain(item => item.Name == "Budget total");
        preview.Removed.Should().Contain(item => item.Name == "Budget validation");
        preview.Removed.Should().Contain(item => item.Name == "Budget group");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Review total");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Review validation");
        preview.Removed.Should().NotContain(item => item.Name == "Review total" || item.Name == "Review validation");
    }

    [Fact]
    public void Option_preview_lists_follow_up_and_dangling_values_but_blocks_scoring_rule_ownership()
    {
        var source = Question(11, 1, "Choose a plan", AnswerType.Checkboxes);
        var option = new QuestionOption { Id = 31, QuestionId = source.Question!.Id, Label = "Premium", Value = "premium", LogicalKey = Guid.NewGuid() };
        source.Question.Options = [option];
        source.Question.Rules = [new ScoringRule { Id = 61, Family = RuleFamily.Selection, Points = 5,
            Options = [new ScoringRuleOption { QuestionOptionId = option.Id }] }];
        var child = Question(12, 1, "Premium details"); child.ParentOptionId = option.Id;
        var category = new Category { Id = 1, Name = "Plan", Elements = [source, child] };
        var calc = new CalculatedField { Id = Guid.NewGuid(), Name = "Premium flag", Sources = [new ValueSource { Kind = SourceKind.OptionSelected, Key = option.LogicalKey }] };
        var graph = Graph([category], new CompositionDefinition { Calculations = [calc] });

        var preview = DraftDeletionImpactAnalyzer.Analyze(graph, DraftDeletionKind.Option, option.Id);

        preview.CanDelete.Should().BeFalse();
        preview.Blocked.Should().Contain(item => item.Detail.Contains("scoring rule", StringComparison.OrdinalIgnoreCase));
        preview.Removed.Should().Contain(item => item.Name == "Premium");
        preview.Removed.Should().Contain(item => item.Name == "Premium details");
        preview.NeedsRepair.Should().Contain(item => item.Name == "Premium flag");
    }

    [Fact]
    public void Incomplete_or_duplicate_composition_identity_blocks_a_false_no_impact_confirmation()
    {
        var target = Question(11, 1, "Target");
        var category = new Category { Id = 1, Name = "Section", Elements = [target] };
        var duplicateId = Guid.NewGuid();
        var definition = new CompositionDefinition
        {
            Calculations = [
                new CalculatedField { Id = duplicateId, Name = "First" },
                new CalculatedField { Id = duplicateId, Name = "Second" }
            ]
        };
        var graph = Graph([category], definition);

        var preview = DraftDeletionImpactAnalyzer.Analyze(graph, DraftDeletionKind.Element, target.Id);

        preview.CanDelete.Should().BeFalse();
        preview.Warnings.Should().ContainSingle().Which.Should().Contain("duplicated identities");
    }

    private static Element Question(int id, int categoryId, string label, AnswerType answerType = AnswerType.Number) => new()
    {
        Id = id, CategoryId = categoryId, Kind = ElementKind.Question, Label = label,
        Question = new Question { Id = id, AnswerType = answerType }
    };
    private static ApplicationVersionGraph Graph(IReadOnlyList<Category> categories, CompositionDefinition composition,
        IReadOnlyList<VisibilityCondition>? conditions = null) => new()
    {
        Version = new ApplicationVersion { Id = 5, ApplicationId = 42, CompositionJson = composition.ToJson() },
        Categories = categories, VisibilityConditions = conditions ?? []
    };
    private static ValueSource QuestionSource(Element element) => new() { Kind = SourceKind.Question, Key = element.LogicalKey };
    private static ValueSource CalculationSource(Guid id) => new() { Kind = SourceKind.Calculation, Key = id };
    private static ValueSource Constant(decimal value) => new() { Kind = SourceKind.Constant, Value = value };
}
