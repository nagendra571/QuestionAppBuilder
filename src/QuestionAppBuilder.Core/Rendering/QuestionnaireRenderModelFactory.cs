using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Core.Rendering;

public sealed class QuestionnaireRenderModelFactory : IQuestionnaireRenderer
{
    public QuestionnaireRenderModel Create(ApplicationVersionGraph definition, IReadOnlyDictionary<int, AnswerValue> answers,
        EvaluationResult evaluation, bool preview)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var categories = definition.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Where(c => evaluation.VisibleCategoryIds.Contains(c.Id))
            .Select((category, index) => new RenderCategory(category.Id, category.Name, index,
                FollowUpStructure.Ordered(category.Elements)
                    .Where(e => e.Question is null || evaluation.VisibleQuestionIds.Contains(e.Question.Id))
                    .Select(element => MapElement(element, answers, category.Elements)).ToArray())).ToArray();
        return new QuestionnaireRenderModel(definition.Version.DisplayName, definition.Version.SubmitLabel,
            definition.Version.ConfirmationText, categories, preview);
    }

    private static RenderElement MapElement(Element element, IReadOnlyDictionary<int, AnswerValue> answers, IReadOnlyList<Element> elements)
    {
        if (element.Question is not { } question) return new RenderElement(element.Id, element.Kind, element.Label, element.HelpText, null);
        answers.TryGetValue(question.Id, out var answer);
        var renderedQuestion = new RenderQuestion(question.Id, element.Label, element.HelpText, question.AnswerType, question.IsRequired,
            question.MaxLength, question.MinValue, question.MaxValue, question.MinSelections, question.MaxSelections,
            question.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).Select(o => new RenderOption(o.Id, o.Label)).ToArray(), answer);
        var parent = FollowUpStructure.Parent(element, elements);
        var option = parent?.Question?.Options.FirstOrDefault(o => o.Id == element.ParentOptionId);
        return new RenderElement(element.Id, element.Kind, element.Label, element.HelpText, renderedQuestion)
        { FollowUpDepth = FollowUpStructure.Depth(element, elements), FollowUpContext = parent is null ? null : $"{parent.Label} / {option?.Label}" };
    }
}

public sealed record QuestionnaireRenderModel(string Title, string SubmitLabel, string ConfirmationText,
    IReadOnlyList<RenderCategory> Categories, bool Preview);
public sealed record RenderCategory(int Id, string Name, int Index, IReadOnlyList<RenderElement> Elements);
public sealed record RenderElement(int Id, QuestionAppBuilder.Domain.Enums.ElementKind Kind, string Label, string? HelpText, RenderQuestion? Question)
{
    public int FollowUpDepth { get; init; }
    public string? FollowUpContext { get; init; }
}
public sealed record RenderQuestion(int Id, string Label, string? HelpText, QuestionAppBuilder.Domain.Enums.AnswerType AnswerType,
    bool Required, int? MaxLength, decimal? MinValue, decimal? MaxValue, int? MinSelections, int? MaxSelections,
    IReadOnlyList<RenderOption> Options, AnswerValue? Value);
public sealed record RenderOption(int Id, string Label);
