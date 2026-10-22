using FluentAssertions;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Evaluation;

public class CompositionTests
{
    [Fact]
    public void Generic_calculations_validate_combined_answers_and_score_a_band_once()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var category = Guid.NewGuid(); var total = Guid.NewGuid(); var share = Guid.NewGuid();
        var version = new ApplicationVersion { Categories = { new Category { Id = 1, LogicalKey = category, Name = "Budget", Elements = {
            new Element { Id = 1, LogicalKey = a, Question = new Question { Id = 1, AnswerType = AnswerType.Number } },
            new Element { Id = 2, LogicalKey = b, Question = new Question { Id = 2, AnswerType = AnswerType.Number } }
        } } } };
        var property = typeof(ApplicationVersion).GetProperty("CompositionJson");
        property.Should().NotBeNull("applications need versioned generic configuration");
        property!.SetValue(version, $$$"""
        {"Calculations":[
          {"Id":"{{{total}}}","Name":"Total","CategoryKey":"{{{category}}}","Operation":"Sum","Sources":[{"Kind":"Question","Key":"{{{a}}}"},{"Kind":"Question","Key":"{{{b}}}"}]},
          {"Id":"{{{share}}}","Name":"First share","CategoryKey":"{{{category}}}","Operation":"Percentage","Sources":[{"Kind":"Question","Key":"{{{a}}}"},{"Kind":"Calculation","Key":"{{{total}}}"}]}],
        "Rules":[
          {"Name":"Capacity","CategoryKey":"{{{category}}}","Kind":"Validation","Message":"Combined request exceeds capacity.","Checks":[{"Left":{"Kind":"Calculation","Key":"{{{total}}}"},"Comparison":"LessThanOrEqual","Right":{"Kind":"Constant","Value":10}}]},
          {"Name":"Higher band","CategoryKey":"{{{category}}}","Kind":"Score","BandGroup":"Allocation","Points":15,"Checks":[{"Left":{"Kind":"Calculation","Key":"{{{share}}}"},"Comparison":"GreaterThanOrEqual","Right":{"Kind":"Constant","Value":75}}]},
          {"Name":"Lower band","CategoryKey":"{{{category}}}","Kind":"Score","BandGroup":"Allocation","Points":10,"Checks":[{"Left":{"Kind":"Calculation","Key":"{{{share}}}"},"Comparison":"GreaterThanOrEqual","Right":{"Kind":"Constant","Value":50}}]}]}
        """);
        var graph = new ApplicationVersionGraph { Version = version, Categories = version.Categories };
        var result = new DefinitionEvaluator().Evaluate(graph, new Dictionary<int, AnswerValue> { [1] = new(null, 9, null), [2] = new(null, 3, null) }, true);
        result.DefinitionIssues.Should().BeEmpty();
        result.AnswerIssues.Should().Contain(i => i.Message == "Combined request exceeds capacity.");
        result.Score.Total.Should().Be(15);
    }
}
