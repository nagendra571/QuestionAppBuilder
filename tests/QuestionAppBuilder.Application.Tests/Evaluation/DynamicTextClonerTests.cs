using FluentAssertions;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Evaluation;

public class DynamicTextClonerTests
{
    [Fact]
    public void Copy_category_remaps_owned_variables_and_templates_to_the_cloned_elements()
    {
        var questionKey = Guid.NewGuid();
        var source = Category(1, questionKey, AnswerType.Textbox);
        var copy = Category(2, Guid.NewGuid(), AnswerType.Textbox);
        var variableId = Guid.NewGuid();
        var definition = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                Variables = [new DynamicVariable
                {
                    Id = variableId, Name = "Organization", CategoryKey = source.LogicalKey,
                    Operation = DynamicOperation.Value, Sources = [new() { Kind = DynamicSourceKind.Question, Key = questionKey }]
                }],
                Templates = [new DynamicTemplate
                {
                    Slot = DynamicTextSlot.ElementLabel, OwnerKey = questionKey,
                    Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variableId } }]
                }]
            }
        };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { CompositionJson = definition.ToJson() }, Categories = [source, copy]
        };

        CompositionCloner.CopyCategory(graph, source, copy);
        var cloned = CompositionDefinition.Read(graph.Version.CompositionJson!).DynamicText;

        cloned.Variables.Should().HaveCount(2);
        var copiedVariable = cloned.Variables.Single(v => v.CategoryKey == copy.LogicalKey);
        copiedVariable.Id.Should().NotBe(variableId);
        copiedVariable.Sources.Single().Key.Should().Be(copy.Elements.Single().LogicalKey);
        cloned.Templates.Should().HaveCount(2);
        var copiedTemplate = cloned.Templates.Single(t => t.OwnerKey == copy.Elements.Single().LogicalKey);
        copiedTemplate.Parts.Single().Source!.Key.Should().Be(copiedVariable.Id);
    }

    [Fact]
    public void Remove_category_prunes_its_dynamic_variables_and_element_templates()
    {
        var categoryKey = Guid.NewGuid();
        var questionKey = Guid.NewGuid();
        var variableId = Guid.NewGuid();
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion
            {
                CompositionJson = new CompositionDefinition
                {
                    DynamicText = new DynamicTextDefinition
                    {
                        Variables = [new() { Id = variableId, Name = "Owned", CategoryKey = categoryKey }],
                        Templates = [new() { Slot = DynamicTextSlot.ElementHelpText, OwnerKey = questionKey }]
                    }
                }.ToJson()
            },
            Categories = []
        };

        CompositionCloner.RemoveCategory(graph, categoryKey, new HashSet<Guid> { questionKey });
        var result = CompositionDefinition.Read(graph.Version.CompositionJson!).DynamicText;

        result.Variables.Should().BeEmpty();
        result.Templates.Should().BeEmpty();
    }

    [Fact]
    public void Copy_elements_clones_referenced_variables_and_remaps_question_and_template_keys()
    {
        var source = Category(1, Guid.NewGuid(), AnswerType.Textbox);
        var copy = Category(2, Guid.NewGuid(), AnswerType.Textbox);
        var variableId = Guid.NewGuid();
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion
            {
                CompositionJson = new CompositionDefinition
                {
                    DynamicText = new DynamicTextDefinition
                    {
                        Variables = [new() { Id = variableId, Name = "Text", CategoryKey = source.LogicalKey,
                            Operation = DynamicOperation.Value, Sources = [new() { Kind = DynamicSourceKind.Question, Key = source.Elements.Single().LogicalKey }] }],
                        Templates = [new() { Slot = DynamicTextSlot.ElementLabel, OwnerKey = source.Elements.Single().LogicalKey,
                            Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variableId } }] }]
                    }
                }.ToJson()
            },
            Categories = [source]
        };

        CompositionCloner.CopyElements(graph, [source.Elements.Single()], [copy.Elements.Single()]);
        var result = CompositionDefinition.Read(graph.Version.CompositionJson!).DynamicText;
        var clonedVariable = result.Variables.Single(variable => variable.Id != variableId);
        var clonedTemplate = result.Templates.Single(template => template.OwnerKey == copy.Elements.Single().LogicalKey);

        clonedVariable.Sources.Single().Key.Should().Be(copy.Elements.Single().LogicalKey);
        clonedTemplate.Parts.Single().Source!.Key.Should().Be(clonedVariable.Id);
        CompositionCloner.RemoveElements(graph, new HashSet<Guid> { copy.Elements.Single().LogicalKey });
        CompositionDefinition.Read(graph.Version.CompositionJson!).DynamicText.Templates.Should().ContainSingle()
            .Which.OwnerKey.Should().Be(source.Elements.Single().LogicalKey);
    }

    [Fact]
    public void Malformed_dynamic_clone_references_fail_with_definition_validation_errors()
    {
        var source = Category(1, Guid.NewGuid(), AnswerType.Textbox);
        var copy = Category(2, Guid.NewGuid(), AnswerType.Textbox);
        var duplicateId = Guid.NewGuid();
        var duplicateGraph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { CompositionJson = new CompositionDefinition
            {
                DynamicText = new DynamicTextDefinition
                {
                    Variables = [new() { Id = duplicateId, Name = "A" }, new() { Id = duplicateId, Name = "B" }]
                }
            }.ToJson() },
            Categories = [source, copy]
        };

        Action duplicate = () => CompositionCloner.CopyCategory(duplicateGraph, source, copy);
        duplicate.Should().Throw<DefinitionValidationException>();

        var danglingGraph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { CompositionJson = new CompositionDefinition
            {
                DynamicText = new DynamicTextDefinition
                {
                    Variables = [new() { Name = "A", CategoryKey = source.LogicalKey, Operation = DynamicOperation.Value,
                        Sources = [new() { Kind = DynamicSourceKind.Question, Key = Guid.NewGuid() }] }]
                }
            }.ToJson() },
            Categories = [source, copy]
        };
        Action dangling = () => CompositionCloner.CopyCategory(danglingGraph, source, copy);
        dangling.Should().Throw<DefinitionValidationException>();
    }

    private static Category Category(int id, Guid key, AnswerType answerType)
    {
        var question = new Question { Id = id, ElementId = id, AnswerType = answerType };
        return new Category
        {
            Id = id, LogicalKey = Guid.NewGuid(), Name = $"Category {id}",
            Elements = [new Element { Id = id, CategoryId = id, Kind = ElementKind.Question, Label = "Name", LogicalKey = key, Question = question }]
        };
    }
}
