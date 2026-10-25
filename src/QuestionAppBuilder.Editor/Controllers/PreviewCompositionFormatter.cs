using System.Globalization;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Controllers;

internal sealed class PreviewCompositionFormatter
{
    private readonly IReadOnlyDictionary<Guid, Element> _questions;
    private readonly IReadOnlyDictionary<Guid, (Element Element, QuestionOption Option)> _options;
    private readonly IReadOnlyDictionary<Guid, CalculatedField> _calculations;

    public PreviewCompositionFormatter(ApplicationVersionGraph graph, CompositionDefinition definition)
    {
        _questions = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).GroupBy(element => element.LogicalKey)
            .ToDictionary(group => group.Key, group => group.First());
        _options = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null)
            .SelectMany(element => element.Question!.Options.Select(option => (element, option)))
            .GroupBy(pair => pair.option.LogicalKey)
            .ToDictionary(group => group.Key, group => (group.First().element, group.First().option));
        _calculations = definition.Calculations?.Where(item => item is not null).GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First()) ?? [];
    }

    public string SourceName(ValueSource source) => source.Kind switch
    {
        SourceKind.Constant => FormatValue(source.Value),
        SourceKind.Question => _questions.TryGetValue(source.Key, out var question) ? question.Label : "missing question",
        SourceKind.OptionSelected => _options.TryGetValue(source.Key, out var option)
            ? $"{option.Option.Label} selected for {option.Element.Label}" : "missing option",
        SourceKind.Calculation => _calculations.TryGetValue(source.Key, out var calculation) ? calculation.Name : "missing calculation",
        _ => "unknown value"
    };

    public string Requirement(ValueCheck check) =>
        $"{SourceName(check.Left)} {ComparisonText(check.Comparison)} {SourceName(check.Right)}";

    public FailedCombinedCheckPreview FormatCheck(ValueCheck check, CompositionCheckEvaluation evaluation) =>
        new(Requirement(check), SourceName(check.Left), evaluation.Left.Value,
            DescribeEffects(evaluation.Left), SourceName(check.Right), evaluation.Right.Value,
            DescribeEffects(evaluation.Right), evaluation.Matches)
        {
            LeftValueText = FormatValue(evaluation.Left.Value),
            RightValueText = FormatValue(evaluation.Right.Value)
        };

    public string FormatValue(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "unavailable";

    private IReadOnlyList<string> DescribeEffects(CompositionValueRead read) => read.Effects.Select(EffectDescription).Distinct().ToArray();

    private string EffectDescription(CompositionInputEffect effect) => effect.Code switch
    {
        "hidden-question" => $"{(_questions.TryGetValue(effect.Key, out var question) ? question.Label : "A hidden question")} was read as 0 because it was hidden.",
        "missing-number" => $"{(_questions.TryGetValue(effect.Key, out var missing) ? missing.Label : "A question")} had no number, so it was read as 0.",
        "question" => $"{(_questions.TryGetValue(effect.Key, out var answered) ? answered.Label : "A question")} supplied {FormatValue(effect.UsedValue)}.",
        "hidden-option" => $"The option for {(_options.TryGetValue(effect.Key, out var hiddenOption) ? hiddenOption.Element.Label : "a hidden question")} was read as 0 because the question was hidden.",
        "unselected-option" => $"{(_options.TryGetValue(effect.Key, out var unselected) ? unselected.Option.Label : "An option")} was not selected and was read as 0.",
        "selected-option" => $"{(_options.TryGetValue(effect.Key, out var selected) ? selected.Option.Label : "An option")} was selected and was read as 1.",
        "calculation" => $"{(_calculations.TryGetValue(effect.Key, out var calculated) ? calculated.Name : "A calculation")} evaluated to {FormatValue(effect.UsedValue)}.",
        "unavailable-calculation" => $"{(_calculations.TryGetValue(effect.Key, out var unavailable) ? unavailable.Name : "A calculation")} was unavailable.",
        _ => "A configured numeric input was unavailable."
    };

    private static string ComparisonText(Comparison comparison) => comparison switch
    {
        Comparison.Equal => "equals", Comparison.NotEqual => "does not equal", Comparison.GreaterThan => "is greater than",
        Comparison.GreaterThanOrEqual => "is at least", Comparison.LessThan => "is less than",
        Comparison.LessThanOrEqual => "is at most", _ => "has an unknown comparison to"
    };
}
