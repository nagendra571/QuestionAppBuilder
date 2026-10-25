using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

public sealed record DraftDeletionEntity(DraftDeletionKind Kind, Guid LogicalKey, Category? Category = null,
    Element? Element = null, QuestionOption? Option = null);

public static class DraftDeletionGraph
{
    public static DraftDeletionEntity? FindTarget(ApplicationVersionGraph graph, DraftDeletionKind kind, int id)
    {
        if (kind == DraftDeletionKind.Category)
        {
            var category = graph.Categories.FirstOrDefault(category => category.Id == id);
            return category is null ? null : new(kind, category.LogicalKey, Category: category);
        }
        foreach (var category in graph.Categories)
        foreach (var element in category.Elements)
        {
            if (kind == DraftDeletionKind.Element && element.Id == id)
                return new(kind, element.LogicalKey, category, element);
            if (kind == DraftDeletionKind.Option && element.Question is { } question)
            {
                var option = question.Options.FirstOrDefault(option => option.Id == id);
                if (option is not null) return new(kind, option.LogicalKey, category, element, option);
            }
        }
        return null;
    }

    public static void Apply(ApplicationVersionGraph graph, DraftDeletionKind kind, int id)
    {
        var target = FindTarget(graph, kind, id) ?? throw new KeyNotFoundException($"{kind} {id} was not found.");
        Apply(graph, target);
    }

    public static void Apply(ApplicationVersionGraph graph, Category category)
    {
        var target = graph.Categories.FirstOrDefault(item => ReferenceEquals(item, category))
            ?? throw new KeyNotFoundException("The category is not part of this draft graph.");
        Apply(graph, new DraftDeletionEntity(DraftDeletionKind.Category, target.LogicalKey, Category: target));
    }

    public static void Apply(ApplicationVersionGraph graph, Element element)
    {
        var category = graph.Categories.FirstOrDefault(item => item.Elements.Any(candidate => ReferenceEquals(candidate, element)))
            ?? throw new KeyNotFoundException("The element is not part of this draft graph.");
        Apply(graph, new DraftDeletionEntity(DraftDeletionKind.Element, element.LogicalKey, category, element));
    }

    public static void Apply(ApplicationVersionGraph graph, QuestionOption option)
    {
        var element = graph.Categories.SelectMany(category => category.Elements)
            .FirstOrDefault(candidate => candidate.Question?.Options.Any(item => ReferenceEquals(item, option)) == true)
            ?? throw new KeyNotFoundException("The option is not part of this draft graph.");
        var category = graph.Categories.Single(candidate => candidate.Elements.Contains(element));
        Apply(graph, new DraftDeletionEntity(DraftDeletionKind.Option, option.LogicalKey, category, element, option));
    }

    private static void Apply(ApplicationVersionGraph graph, DraftDeletionEntity target)
    {
        var kind = target.Kind;
        switch (kind)
        {
            case DraftDeletionKind.Category:
            {
                var category = target.Category!;
                var removedElements = category.Elements.Select(element => element.Id).ToHashSet();
                var removedQuestions = category.Elements.Where(element => element.Question is not null).Select(element => element.Question!.Id).ToHashSet();
                var removedOptions = category.Elements.Where(element => element.Question is not null).SelectMany(element => element.Question!.Options).Select(option => option.Id).ToHashSet();
                var removedQuestionKeys = category.Elements.Where(element => element.Question is not null).Select(element => element.LogicalKey).ToHashSet();
                CompositionCloner.RemoveCategory(graph, category.LogicalKey, removedQuestionKeys);
                PruneVisibilityConditions(graph, removedQuestions, removedOptions, removedElements, new HashSet<int> { category.Id });
                graph.Version.Categories.Remove(category);
                break;
            }
            case DraftDeletionKind.Element:
            {
                var element = target.Element!;
                RemoveBranch(graph, target.Category!, FollowUpStructure.Branch(element, target.Category!.Elements));
                break;
            }
            case DraftDeletionKind.Option:
            {
                var option = target.Option!;
                if (IsOptionReferencedByRule(graph, option))
                    throw new DefinitionValidationException($"Option {option.Id} is used by a scoring rule; remove or change the rule first.");
                var question = target.Element!.Question!;
                var category = target.Category!;
                var removed = category.Elements.Where(element => element.ParentOptionId == option.Id)
                    .SelectMany(element => FollowUpStructure.Branch(element, category.Elements)).Distinct().ToArray();
                RemoveBranch(graph, category, removed);
                PruneVisibilityConditions(graph, new HashSet<int>(), new HashSet<int> { option.Id }, new HashSet<int>(), new HashSet<int>());
                question.Options.Remove(option);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void RemoveBranch(ApplicationVersionGraph graph, Category category, IReadOnlyCollection<Element> removed)
    {
        var questions = removed.Where(element => element.Question is not null).Select(element => element.Question!).ToArray();
        CompositionCloner.RemoveElements(graph, removed.Select(element => element.LogicalKey).ToHashSet());
        PruneVisibilityConditions(graph, questions.Select(question => question.Id).ToHashSet(),
            questions.SelectMany(question => question.Options).Select(option => option.Id).ToHashSet(),
            removed.Select(element => element.Id).ToHashSet(), new HashSet<int>());
        foreach (var element in removed) { element.ParentOption = null; element.ParentOptionId = null; category.Elements.Remove(element); }
        NormalizeElements(category);
    }

    private static void PruneVisibilityConditions(ApplicationVersionGraph graph, ISet<int> removedQuestionIds, ISet<int> removedOptionIds,
        ISet<int> removedElementIds, ISet<int> removedCategoryIds)
    {
        var conditionIds = graph.VisibilityConditions
            .Where(condition => condition.Predicates.Any(predicate => removedQuestionIds.Contains(predicate.SourceQuestionId)
                || predicate.Options.Any(option => removedOptionIds.Contains(option.QuestionOptionId))))
            .Select(condition => condition.Id).ToHashSet();
        conditionIds.UnionWith(graph.Categories.Where(category => removedCategoryIds.Contains(category.Id) && category.VisibilityConditionId is not null)
            .Select(category => category.VisibilityConditionId!.Value));
        conditionIds.UnionWith(graph.Categories.SelectMany(category => category.Elements)
            .Where(element => removedElementIds.Contains(element.Id) && element.VisibilityConditionId is not null)
            .Select(element => element.VisibilityConditionId!.Value));
        foreach (var category in graph.Categories)
        {
            if (category.VisibilityConditionId is int categoryConditionId && conditionIds.Contains(categoryConditionId))
            { category.VisibilityConditionId = null; category.VisibilityCondition = null; }
            foreach (var element in category.Elements)
                if (element.VisibilityConditionId is int elementConditionId && conditionIds.Contains(elementConditionId))
                { element.VisibilityConditionId = null; element.VisibilityCondition = null; }
        }
        graph.Version.VisibilityConditions.RemoveAll(condition => conditionIds.Contains(condition.Id));
    }

    private static void NormalizeElements(Category category)
    {
        var ordered = FollowUpStructure.Ordered(category.Elements);
        for (var index = 0; index < ordered.Count; index++) ordered[index].SortOrder = index + 1;
    }

    public static bool IsOptionReferencedByRule(ApplicationVersionGraph graph, QuestionOption option)
        => graph.Categories.SelectMany(category => category.Elements).Select(element => element.Question).Where(question => question is not null)
            .SelectMany(question => question!.Rules).Any(rule => rule.Options.Any(link => link.QuestionOptionId == option.Id));
}
