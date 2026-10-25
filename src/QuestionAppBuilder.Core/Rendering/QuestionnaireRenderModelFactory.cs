using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Composition;
using System.Globalization;

namespace QuestionAppBuilder.Core.Rendering;

public sealed class QuestionnaireRenderModelFactory : IQuestionnaireRenderer
{
    public QuestionnaireRenderModel Create(ApplicationVersionGraph definition, IReadOnlyDictionary<int, AnswerValue> answers,
        EvaluationResult evaluation, bool preview)
    {
        ArgumentNullException.ThrowIfNull(definition);
        QuestionAppBuilder.Domain.Composition.CompositionDefinition composition;
        try { composition = QuestionAppBuilder.Domain.Composition.CompositionDefinition.Read(definition.Version.CompositionJson); }
        catch (System.Text.Json.JsonException) { composition = new(); }
        var dynamicTexts = evaluation.Composition?.DynamicText?.Texts
            .ToDictionary(text => (text.Slot, text.OwnerKey)) ?? new Dictionary<(DynamicTextSlot Slot, Guid OwnerKey), QuestionAppBuilder.Application.Composition.ResolvedDynamicText>();
        string Resolve(DynamicTextSlot slot, Guid ownerKey, string fallback)
            => dynamicTexts.TryGetValue((slot, ownerKey), out var text) ? text.Text : fallback;
        var categories = definition.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Where(c => evaluation.VisibleCategoryIds.Contains(c.Id))
            .Select((category, index) => new RenderCategory(category.Id, category.Name, index,
                FollowUpStructure.Ordered(category.Elements)
                    .Where(e => evaluation.VisibleElementIds is { } visibleElements
                        ? visibleElements.Contains(e.Id)
                        : e.Question is null || evaluation.VisibleQuestionIds.Contains(e.Question.Id))
                    .Select(element => MapElement(element, answers, category.Elements, Resolve, dynamicTexts)).ToArray()) {
                    Groups = evaluation.Composition?.DefinitionErrors.Count == 0
                        ? (composition.Groups ?? []).Where(g => g is not null && g.CategoryKey == category.LogicalKey).ToArray()
                        : [],
                    CalculatedValues = evaluation.Composition?.Values.Where(v => v.CategoryKey == category.LogicalKey).ToArray() ?? []
                }).ToArray();
        var review = dynamicTexts.GetValueOrDefault((DynamicTextSlot.ReviewSummary, Guid.Empty));
        var confirmation = Resolve(DynamicTextSlot.ConfirmationText, Guid.Empty, definition.Version.ConfirmationText);
        return new QuestionnaireRenderModel(definition.Version.DisplayName, definition.Version.SubmitLabel,
            confirmation, categories, preview)
        {
            ReviewSummary = review?.Text,
            HideReviewSummary = review?.IsHidden == true
        };
    }

    private static RenderElement MapElement(Element element, IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlyList<Element> elements,
        Func<DynamicTextSlot, Guid, string, string> resolve,
        IReadOnlyDictionary<(DynamicTextSlot Slot, Guid OwnerKey), QuestionAppBuilder.Application.Composition.ResolvedDynamicText> dynamicTexts)
    {
        var label = resolve(DynamicTextSlot.ElementLabel, element.LogicalKey, element.Label);
        var help = resolve(DynamicTextSlot.ElementHelpText, element.LogicalKey, element.HelpText ?? string.Empty);
        var isHidden = dynamicTexts.TryGetValue((DynamicTextSlot.ElementLabel, element.LogicalKey), out var dynamicLabel) && dynamicLabel.IsHidden;
        if (element.Question is not { } question)
            return new RenderElement(element.Id, element.Kind, label, string.IsNullOrEmpty(help) ? null : help, null)
            { LogicalKey = element.LogicalKey, IsHidden = isHidden };
        answers.TryGetValue(question.Id, out var answer);
        var displayDate = question.AnswerType == AnswerType.Date && DateOnly.TryParseExact(answer?.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture) : null;
        var renderedQuestion = new RenderQuestion(question.Id, label, string.IsNullOrEmpty(help) ? null : help, question.AnswerType, question.IsRequired,
            question.MaxLength, question.MinValue, question.MaxValue, question.MinSelections, question.MaxSelections,
            question.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).Select(o => new RenderOption(o.Id, o.Label)).ToArray(), answer)
            { WholeNumbersOnly = question.WholeNumbersOnly, DateDisplayValue = displayDate };
        var parent = FollowUpStructure.Parent(element, elements);
        var option = parent?.Question?.Options.FirstOrDefault(o => o.Id == element.ParentOptionId);
        var parentLabel = parent is null ? null : resolve(DynamicTextSlot.ElementLabel, parent.LogicalKey, parent.Label);
        return new RenderElement(element.Id, element.Kind, label, string.IsNullOrEmpty(help) ? null : help, renderedQuestion)
        { LogicalKey = element.LogicalKey, IsHidden = isHidden, FollowUpDepth = FollowUpStructure.Depth(element, elements), FollowUpContext = parent is null ? null : $"{parentLabel} / {option?.Label}" };
    }
}

public sealed record QuestionnaireRenderModel(string Title, string SubmitLabel, string ConfirmationText,
    IReadOnlyList<RenderCategory> Categories, bool Preview)
{
    public string? ReviewSummary { get; init; }
    public bool HideReviewSummary { get; init; }
}
public sealed record RenderCategory(int Id, string Name, int Index, IReadOnlyList<RenderElement> Elements)
{
    public IReadOnlyList<QuestionAppBuilder.Domain.Composition.QuestionGroup> Groups { get; init; } = [];
    public IReadOnlyList<QuestionAppBuilder.Application.Composition.CalculatedValue> CalculatedValues { get; init; } = [];
}
public sealed record RenderElement(int Id, QuestionAppBuilder.Domain.Enums.ElementKind Kind, string Label, string? HelpText, RenderQuestion? Question)
{
    public Guid LogicalKey { get; init; }
    public bool IsHidden { get; init; }
    public int FollowUpDepth { get; init; }
    public string? FollowUpContext { get; init; }
}
public sealed record RenderQuestion(int Id, string Label, string? HelpText, QuestionAppBuilder.Domain.Enums.AnswerType AnswerType,
    bool Required, int? MaxLength, decimal? MinValue, decimal? MaxValue, int? MinSelections, int? MaxSelections,
    IReadOnlyList<RenderOption> Options, AnswerValue? Value)
{
    public bool WholeNumbersOnly { get; init; }
    public string? DateDisplayValue { get; init; }
}
public sealed record RenderOption(int Id, string Label);
