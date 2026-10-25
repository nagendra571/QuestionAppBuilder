using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DefinitionSearchItemKind
{
    Category, Question, Content, Option, QuestionScoring, Visibility, Group, Calculation,
    CombinedRule, DynamicVariable, DynamicTemplate
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DefinitionSearchMatchKind { Own, Reference }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DefinitionSearchDestinationKind { Workspace, Composition }

public sealed record DefinitionSearchDestination(DefinitionSearchDestinationKind Kind, string Fragment);

public sealed record DefinitionSearchItem(
    DefinitionSearchItemKind Kind,
    string Name,
    string MatchedText,
    DefinitionSearchMatchKind MatchKind,
    string? ReferenceLabel,
    string? Context,
    DefinitionSearchDestination Destination);

public sealed record DefinitionSearchReport(
    int VersionId,
    IReadOnlyList<DefinitionSearchItem> Items,
    int TotalCount,
    IReadOnlyList<string> Warnings,
    bool Complete);

/// <summary>Searches one loaded draft graph and its direct, currently resolvable references.</summary>
public static class DefinitionSearcher
{
    public const int MaximumQueryLength = 200;
    public const int MaximumResults = 50;

    public static DefinitionSearchReport Search(ApplicationVersionGraph graph, string query)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(query);
        var term = query.Trim();
        if (term.Length is < 1 or > MaximumQueryLength)
            throw new ArgumentException($"Search text must contain between 1 and {MaximumQueryLength} characters.", nameof(query));

        var entries = new List<SearchEntry>();
        var entryKeys = new HashSet<string>(StringComparer.Ordinal);
        var duplicateEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var graphWarning = false;
        var compositionWarning = false;
        void WarnGraph() => graphWarning = true;
        void WarnComposition() => compositionWarning = true;
        void Add(string identity, DefinitionSearchItemKind kind, string name, IEnumerable<string?> ownText,
            IEnumerable<string?> references, string? context, DefinitionSearchDestination destination)
        {
            if (duplicateEntryKeys.Contains(identity)) return;
            if (!entryKeys.Add(identity))
            {
                entries.RemoveAll(x => x.Identity == identity);
                duplicateEntryKeys.Add(identity);
                WarnGraph();
                return;
            }
            entries.Add(new SearchEntry(identity, kind, name, Clean(ownText), Clean(references), context, destination));
        }

        var categories = graph.Categories ?? Array.Empty<Category>();
        if (graph.Categories is null) WarnGraph();
        if (categories.Any(x => x is null)) WarnGraph();
        var validCategories = categories.Where(x => x is not null).ToArray();
        var elements = new List<(Category Category, Element Element)>();
        foreach (var category in validCategories)
        {
            if (category.Elements is null)
            {
                WarnGraph();
                continue;
            }
            if (category.Elements.Any(x => x is null)) WarnGraph();
            elements.AddRange(category.Elements.Where(x => x is not null).Select(element => (category, element)));
        }

        var categoryByKey = Index(validCategories, x => x.LogicalKey, WarnGraph);
        var elementByKey = Index(elements.Select(x => x.Element), x => x.LogicalKey, WarnGraph);
        var questionElementByKey = Index(elements.Where(x => x.Element.Question is not null).Select(x => x.Element), x => x.LogicalKey, WarnGraph);
        var elementByQuestionId = Index(elements.Where(x => x.Element.Question is not null), x => x.Element.Question!.Id,
            WarnGraph, x => x.Element);
        var optionRows = new List<(Category Category, Element Element, Question Question, QuestionOption Option)>();
        foreach (var (category, element) in elements)
        {
            var question = element.Question;
            if (question is null) continue;
            if (question.Options is null || question.Rules is null) WarnGraph();
            foreach (var option in question.Options ?? [])
            {
                if (option is null) { WarnGraph(); continue; }
                optionRows.Add((category, element, question, option));
            }
        }
        var optionById = Index(optionRows, x => x.Option.Id, WarnGraph);
        var optionByKey = Index(optionRows, x => x.Option.LogicalKey, WarnGraph);
        var optionLabelsById = optionById.ToDictionary(pair => pair.Key, pair => pair.Value.Option.Label);
        var optionLabelsByKey = optionByKey.ToDictionary(pair => pair.Key, pair => pair.Value.Option.Label);

        var conditionById = Index((graph.VisibilityConditions ?? Array.Empty<VisibilityCondition>())
            .Where(x => x is not null), x => x.Id, WarnGraph);
        if (graph.VisibilityConditions is null || graph.VisibilityConditions.Any(x => x is null)) WarnGraph();
        var visibilityReferences = conditionById.ToDictionary(pair => pair.Key,
            pair => BuildVisibilityReferences(pair.Value, elementByQuestionId, optionLabelsById, WarnGraph).ToArray());

        // Base definition entries and question-owned scoring rules.
        foreach (var category in validCategories)
        {
            if (category.Id <= 0) { WarnGraph(); continue; }
            var categoryContext = null as string;
            var categoryReferences = GetVisibilityReferences(category.VisibilityConditionId, visibilityReferences);
            Add($"category:{category.Id}", DefinitionSearchItemKind.Category, Display(category.Name, "Unnamed category"),
                [category.Name], categoryReferences, categoryContext, Workspace($"category-{category.Id}"));
        }

        foreach (var (category, element) in elements)
        {
            if (element.Id <= 0) { WarnGraph(); continue; }
            var isQuestion = element.Question is not null;
            var kind = isQuestion ? DefinitionSearchItemKind.Question : DefinitionSearchItemKind.Content;
            var name = Display(element.Label, isQuestion ? "Unnamed question" : "Unnamed content block");
            var references = new List<string?> { category.Name };
            if (element.ParentOptionId is int parentId)
            {
                if (optionById.TryGetValue(parentId, out var parent))
                    references.Add($"Parent question: {Display(parent.Element.Label, "Unnamed question")} — option: {Display(parent.Option.Label, "Unnamed option")}");
                else WarnGraph();
            }
            references.AddRange(GetVisibilityReferences(element.VisibilityConditionId, visibilityReferences));
            Add($"element:{element.Id}", kind, name,
                [element.Label, element.HelpText, element.Question?.RequiredMessage], references,
                Display(category.Name, "Unnamed category"), Workspace($"element-{element.Id}"));

            if (element.Question is not { } question) continue;
            foreach (var option in question.Options ?? [])
            {
                if (option is null) continue;
                if (option.Id <= 0) { WarnGraph(); continue; }
                Add($"option:{option.Id}", DefinitionSearchItemKind.Option, Display(option.Label, "Unnamed option"),
                    [option.Label, option.Value], [element.Label, category.Name],
                    $"{Display(element.Label, "Unnamed question")} · {Display(category.Name, "Unnamed category")}",
                    Workspace($"option-{option.Id}"));
            }
            foreach (var rule in question.Rules ?? [])
            {
                if (rule is null) { WarnGraph(); continue; }
                if (rule.Id <= 0) { WarnGraph(); continue; }
                var ruleReferences = new List<string?> { element.Label, category.Name };
                if (rule.Options is null) WarnGraph();
                else foreach (var linked in rule.Options)
                {
                    if (linked is null) { WarnGraph(); continue; }
                    if (optionById.TryGetValue(linked.QuestionOptionId, out var option)
                        && option.Question.Id == question.Id) ruleReferences.Add(option.Option.Label);
                    else WarnGraph();
                }
                Add($"scoring:{rule.Id}", DefinitionSearchItemKind.QuestionScoring,
                    $"{Display(element.Label, "Unnamed question")} · {rule.Family} scoring",
                    [rule.Family.ToString(), rule.AwardMethod.ToString(), rule.Points.ToString(CultureInfo.InvariantCulture),
                        rule.PointsPerUnit.ToString(CultureInfo.InvariantCulture), rule.MaximumPoints?.ToString(CultureInfo.InvariantCulture),
                        rule.MinValue?.ToString(CultureInfo.InvariantCulture), rule.MaxValue?.ToString(CultureInfo.InvariantCulture)],
                    ruleReferences, Display(category.Name, "Unnamed category"), Workspace($"rule-{rule.Id}"));
            }
        }

        // Each stored condition appears once, including currently unassigned conditions.
        var visibilityTargets = new Dictionary<int, List<string>>();
        foreach (var category in validCategories.Where(x => x.VisibilityConditionId is not null))
        {
            var id = category.VisibilityConditionId!.Value;
            if (conditionById.ContainsKey(id)) AddTarget(id, category.Name);
            else WarnGraph();
        }
        foreach (var (category, element) in elements.Where(x => x.Element.VisibilityConditionId is not null))
        {
            var id = element.VisibilityConditionId!.Value;
            if (conditionById.ContainsKey(id)) AddTarget(id, element.Label);
            else WarnGraph();
        }
        foreach (var condition in conditionById.Values)
        {
            var targets = visibilityTargets.GetValueOrDefault(condition.Id) ?? [];
            var predicateText = (condition.Predicates ?? []).Where(x => x is not null).Select(predicate =>
                $"{predicate.Operator} {predicate.NumberValue?.ToString(CultureInfo.InvariantCulture)}").ToArray();
            if (condition.Predicates is null || condition.Predicates.Any(x => x is null)) WarnGraph();
            Add($"visibility:{condition.Id}", DefinitionSearchItemKind.Visibility,
                "Visibility condition", ["visibility", condition.Junction.ToString(), .. predicateText],
                [.. (visibilityReferences.GetValueOrDefault(condition.Id) ?? []), .. targets],
                targets.Count == 0 ? "Unassigned" : string.Join("; ", targets), Workspace($"condition-{condition.Id}"));
        }

        void AddTarget(int conditionId, string? targetName)
        {
            if (!visibilityTargets.TryGetValue(conditionId, out var targets))
                visibilityTargets[conditionId] = targets = [];
            if (!string.IsNullOrWhiteSpace(targetName)) targets.Add(targetName.Trim());
        }

        CompositionDefinition? composition = null;
        try { composition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (JsonException) { WarnComposition(); }
        if (composition is not null)
        {
            AddCompositionEntries(composition);
        }

        void AddCompositionEntries(CompositionDefinition definition)
        {
            var groups = definition.Groups ?? [];
            var calculations = definition.Calculations ?? [];
            var rules = definition.Rules ?? [];
            var dynamic = definition.DynamicText;
            if (definition.Groups is null || definition.Calculations is null || definition.Rules is null || dynamic is null)
                WarnComposition();
            var calculationById = Index(calculations.Where(x => x is not null), x => x.Id, WarnComposition);
            var variableSource = dynamic?.Variables ?? [];
            var variableById = Index(variableSource.Where(x => x is not null), x => x.Id, WarnComposition);

            foreach (var group in groups)
            {
                if (group is null) { WarnComposition(); continue; }
                if (group.Id == Guid.Empty) { WarnComposition(); continue; }
                if (group.Rows is null || group.Columns is null) WarnComposition();
                var references = new List<string?>();
                if (categoryByKey.TryGetValue(group.CategoryKey, out var groupCategory)) references.Add(groupCategory.Name);
                else WarnComposition();
                foreach (var row in group.Rows ?? [])
                {
                    if (row is null) { WarnComposition(); continue; }
                    if (row.Cells is null) WarnComposition();
                    if (questionElementByKey.TryGetValue(row.QuestionKey, out var member)) references.Add(member.Label);
                    else WarnComposition();
                }
                Add($"group:{group.Id}", DefinitionSearchItemKind.Group, Display(group.Name, "Unnamed group"),
                    [group.Name, .. (group.Columns ?? []), .. (group.Rows ?? []).Where(x => x is not null).SelectMany(x => x.Cells ?? [])],
                    references, groupCategory?.Name, Composition($"Groups/{group.Id:D}"));
            }

            foreach (var calculation in calculations)
            {
                if (calculation is null) { WarnComposition(); continue; }
                if (calculation.Id == Guid.Empty) { WarnComposition(); continue; }
                if (calculation.Sources is null) WarnComposition();
                var references = new List<string?>();
                var categoryName = categoryByKey.TryGetValue(calculation.CategoryKey, out var ownerCategory)
                    ? ownerCategory.Name : null;
                if (categoryName is not null) references.Add(categoryName); else WarnComposition();
                foreach (var source in calculation.Sources ?? [])
                {
                    if (source is null) { WarnComposition(); continue; }
                    AddValueReference(source, references, questionElementByKey, optionLabelsByKey, calculationById, WarnComposition);
                }
                Add($"calculation:{calculation.Id}", DefinitionSearchItemKind.Calculation, Display(calculation.Name, "Unnamed calculation"),
                    [calculation.Name, calculation.Operation.ToString()], references, categoryName,
                    Composition($"Calculations/{calculation.Id:D}"));
            }

            foreach (var rule in rules)
            {
                if (rule is null) { WarnComposition(); continue; }
                if (rule.Id == Guid.Empty) { WarnComposition(); continue; }
                if (rule.Conditions is null || rule.Checks is null) WarnComposition();
                var references = new List<string?>();
                var categoryName = categoryByKey.TryGetValue(rule.CategoryKey, out var ownerCategory)
                    ? ownerCategory.Name : null;
                if (categoryName is not null) references.Add(categoryName); else WarnComposition();
                if (rule.TargetQuestionKey != Guid.Empty)
                {
                    if (questionElementByKey.TryGetValue(rule.TargetQuestionKey, out var target)) references.Add(target.Label);
                    else WarnComposition();
                }
                AddChecks(rule.Conditions ?? [], references);
                AddChecks(rule.Checks ?? [], references);
                Add($"combined-rule:{rule.Id}", DefinitionSearchItemKind.CombinedRule, Display(rule.Name, "Unnamed combined rule"),
                    [rule.Name, rule.Message, rule.BandGroup, rule.Kind.ToString(), rule.Points.ToString(CultureInfo.InvariantCulture),
                        rule.ShowWhileAnswering ? "live feedback" : "no live feedback", .. (rule.Conditions ?? []).Select(CheckText), .. (rule.Checks ?? []).Select(CheckText)],
                    references, categoryName, Composition($"Rules/{rule.Id:D}"));
            }

            if (dynamic is null) return;
            if (dynamic.Variables is null || dynamic.Templates is null) WarnComposition();
            foreach (var variable in dynamic.Variables ?? [])
            {
                if (variable is null) { WarnComposition(); continue; }
                if (variable.Id == Guid.Empty) { WarnComposition(); continue; }
                if (variable.Sources is null) WarnComposition();
                var references = new List<string?>();
                string? categoryName = null;
                if (variable.CategoryKey is Guid categoryKey)
                {
                    if (categoryByKey.TryGetValue(categoryKey, out var ownerCategory))
                    { categoryName = ownerCategory.Name; references.Add(categoryName); }
                    else WarnComposition();
                }
                foreach (var source in variable.Sources ?? [])
                {
                    if (source is null) { WarnComposition(); continue; }
                    AddDynamicReference(source, references, questionElementByKey, calculationById, variableById, WarnComposition);
                }
                Add($"dynamic-variable:{variable.Id}", DefinitionSearchItemKind.DynamicVariable,
                    Display(variable.Name, "Unnamed dynamic value"),
                    [variable.Name, variable.Operation.ToString(), variable.Separator, variable.DateFormat,
                        .. (variable.Sources ?? []).Where(x => x is not null).SelectMany(DynamicLiteralText)],
                    references, categoryName, Composition($"DynamicText/Variables/{variable.Id:D}"));
            }

            foreach (var template in dynamic.Templates ?? [])
            {
                if (template is null) { WarnComposition(); continue; }
                if (!Enum.IsDefined(template.Slot)) { WarnComposition(); continue; }
                var references = new List<string?>();
                var owner = elementByKey.GetValueOrDefault(template.OwnerKey);
                if (template.Slot is DynamicTextSlot.ElementLabel or DynamicTextSlot.ElementHelpText)
                {
                    if (owner is null) { WarnComposition(); continue; }
                    references.Add(owner.Label);
                }
                else if (template.OwnerKey != Guid.Empty) { WarnComposition(); continue; }
                if (template.Parts is null) WarnComposition();
                foreach (var part in template.Parts ?? [])
                {
                    if (part is null) { WarnComposition(); continue; }
                    if (part.Kind == DynamicPartKind.Value && part.Source is { } source)
                        AddDynamicReference(source, references, questionElementByKey, calculationById, variableById, WarnComposition);
                    else if (part.Kind == DynamicPartKind.Value) WarnComposition();
                }
                var templateName = template.Slot switch
                {
                    DynamicTextSlot.ElementLabel => $"Dynamic label · {Display(owner?.Label, "Unnamed element")}",
                    DynamicTextSlot.ElementHelpText => $"Dynamic help text · {Display(owner?.Label, "Unnamed element")}",
                    DynamicTextSlot.ReviewSummary => "Review summary template",
                    DynamicTextSlot.ConfirmationText => "Confirmation text template",
                    _ => "Dynamic text template"
                };
                var templateOwnText = (template.Parts ?? []).Where(x => x is not null)
                    .SelectMany(x => new[] { x.Text, x.Fallback }.Concat(x.Kind == DynamicPartKind.Value && x.Source is not null
                        ? DynamicLiteralText(x.Source) : []));
                Add($"dynamic-template:{template.Slot}:{template.OwnerKey:D}", DefinitionSearchItemKind.DynamicTemplate,
                    templateName, [template.Slot.ToString(), .. templateOwnText],
                    references, owner?.Label, Composition($"DynamicText/Templates/{template.Slot}/{template.OwnerKey:D}"));
            }

            void AddChecks(IEnumerable<ValueCheck> checks, List<string?> references)
            {
                foreach (var check in checks)
                {
                    if (check is null || check.Left is null || check.Right is null) { WarnComposition(); continue; }
                    AddValueReference(check.Left, references, questionElementByKey, optionLabelsByKey, calculationById, WarnComposition);
                    AddValueReference(check.Right, references, questionElementByKey, optionLabelsByKey, calculationById, WarnComposition);
                }
            }
        }

        if (graphWarning) warnings.Add("Some entries or direct references were incomplete or ambiguous; valid entries were searched.");
        if (compositionWarning) warnings.Add("Some composition entries were malformed, incomplete, duplicated, or referenced missing items; valid entries were searched.");

        var matches = entries.Select(entry => Match(entry, term)).Where(match => match is not null)
            .Select(match => match!).OrderBy(match => match.Rank)
            .ThenBy(match => match.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entry.Kind)
            .ThenBy(match => match.Entry.Identity, StringComparer.Ordinal)
            .ToArray();
        var resultItems = matches.Take(MaximumResults).Select(match => new DefinitionSearchItem(
            match.Entry.Kind, match.Entry.Name, match.MatchedText, match.MatchKind, match.ReferenceLabel,
            match.Entry.Context, match.Entry.Destination)).ToArray();
        var truncated = matches.Length > MaximumResults;
        if (truncated) warnings.Add($"Results are limited to {MaximumResults}; refine the search text to see more matches.");
        var finalWarnings = warnings.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new DefinitionSearchReport(graph.Version.Id, resultItems, matches.Length, finalWarnings,
            !truncated && finalWarnings.Length == 0);
    }

    private static void AddValueReference(ValueSource source, List<string?> references,
        IReadOnlyDictionary<Guid, Element> elements, IReadOnlyDictionary<Guid, string> optionLabels,
        IReadOnlyDictionary<Guid, CalculatedField> calculations, Action warn)
    {
        switch (source.Kind)
        {
            case SourceKind.Question:
                if (elements.TryGetValue(source.Key, out var element)) references.Add(element.Label); else warn();
                break;
            case SourceKind.OptionSelected:
                if (optionLabels.TryGetValue(source.Key, out var optionLabel)) references.Add(optionLabel); else warn();
                break;
            case SourceKind.Calculation:
                if (calculations.TryGetValue(source.Key, out var calculation)) references.Add(calculation.Name); else warn();
                break;
            case SourceKind.Constant:
                break;
            default: warn(); break;
        }
    }

    private static void AddDynamicReference(DynamicSource source, List<string?> references,
        IReadOnlyDictionary<Guid, Element> elements, IReadOnlyDictionary<Guid, CalculatedField> calculations,
        IReadOnlyDictionary<Guid, DynamicVariable> variables, Action warn)
    {
        switch (source.Kind)
        {
            case DynamicSourceKind.Question:
                if (elements.TryGetValue(source.Key, out var element)) references.Add(element.Label); else warn();
                break;
            case DynamicSourceKind.Calculation:
                if (calculations.TryGetValue(source.Key, out var calculation)) references.Add(calculation.Name); else warn();
                break;
            case DynamicSourceKind.Variable:
                if (variables.TryGetValue(source.Key, out var variable)) references.Add(variable.Name); else warn();
                break;
            case DynamicSourceKind.Text:
            case DynamicSourceKind.Number:
            case DynamicSourceKind.Date:
            case DynamicSourceKind.Today:
                break;
            default: warn(); break;
        }
    }

    private static IEnumerable<string?> GetVisibilityReferences(int? conditionId,
        IReadOnlyDictionary<int, string?[]> visibilityReferences)
    {
        if (conditionId is not int id) return [];
        return visibilityReferences.TryGetValue(id, out var references) ? references : [];
    }

    private static IEnumerable<string?> BuildVisibilityReferences(VisibilityCondition condition,
        IReadOnlyDictionary<int, Element> questionElements,
        IReadOnlyDictionary<int, string> optionLabels, Action warn)
    {
        var references = new List<string?>();
        if (condition.Predicates is null) { warn(); return references; }
        foreach (var predicate in condition.Predicates)
        {
            if (predicate is null) { warn(); continue; }
            if (questionElements.TryGetValue(predicate.SourceQuestionId, out var source)) references.Add(source.Label);
            else warn();
            if (predicate.Options is null) { warn(); continue; }
            foreach (var link in predicate.Options)
            {
                if (link is null) { warn(); continue; }
                if (optionLabels.TryGetValue(link.QuestionOptionId, out var optionLabel)) references.Add(optionLabel);
                else warn();
            }
        }
        return references;
    }

    private static string CheckText(ValueCheck check)
    {
        static string SourceText(ValueSource source) => source.Kind == SourceKind.Constant
            ? source.Value.ToString(CultureInfo.InvariantCulture) : source.Kind.ToString();
        return check is null || check.Left is null || check.Right is null
            ? string.Empty : $"{SourceText(check.Left)} {check.Comparison} {SourceText(check.Right)}";
    }

    private static IEnumerable<string?> DynamicLiteralText(DynamicSource source) => source.Kind switch
    {
        DynamicSourceKind.Text => [source.Text],
        DynamicSourceKind.Number => [source.Number?.ToString(CultureInfo.InvariantCulture)],
        DynamicSourceKind.Date => [source.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)],
        _ => []
    };

    private static MatchResult? Match(SearchEntry entry, string query)
    {
        if (entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return new(entry, 0, DefinitionSearchMatchKind.Own, Excerpt(entry.Name, 240), null);
        if (entry.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return new(entry, 1, DefinitionSearchMatchKind.Own, Excerpt(entry.Name, 240), null);
        var own = entry.OwnText.FirstOrDefault(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (own is not null) return new(entry, 2, DefinitionSearchMatchKind.Own, ExcerptAround(own, query, 240), null);
        var matchedReferences = entry.References.Where(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return matchedReferences.Length == 0 ? null : new(entry, 3, DefinitionSearchMatchKind.Reference,
            ExcerptAround(matchedReferences[0], query, 240), ExcerptAround(string.Join("; ", matchedReferences), query, 500));
    }

    private static Dictionary<TKey, TItem> Index<TItem, TKey>(IEnumerable<TItem> items,
        Func<TItem, TKey> keySelector, Action warn) where TKey : notnull
        => Index<TItem, TKey, TItem>(items, keySelector, warn, item => item);

    private static Dictionary<TKey, TValue> Index<TItem, TKey, TValue>(IEnumerable<TItem> items,
        Func<TItem, TKey> keySelector, Action warn, Func<TItem, TValue> valueSelector) where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        var ambiguous = new HashSet<TKey>();
        foreach (var item in items)
        {
            if (item is null) { warn(); continue; }
            var key = keySelector(item);
            if ((key is int intKey && intKey <= 0) || (key is Guid guidKey && guidKey == Guid.Empty))
            { warn(); continue; }
            if (ambiguous.Contains(key)) { warn(); continue; }
            if (!result.TryAdd(key, valueSelector(item)))
            {
                result.Remove(key);
                ambiguous.Add(key);
                warn();
            }
        }
        return result;
    }

    private static List<string> Clean(IEnumerable<string?> values) => values.Where(x => !string.IsNullOrEmpty(x))
        .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Display(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string Excerpt(string value, int limit) => value.Length <= limit ? value : value[..(limit - 1)] + "…";
    private static string ExcerptAround(string value, string query, int limit)
    {
        if (value.Length <= limit) return value;
        var match = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (match < 0) return Excerpt(value, limit);
        var leading = match > 0;
        var trailing = match + query.Length < value.Length;
        var budget = limit - (leading ? 1 : 0) - (trailing ? 1 : 0);
        var start = Math.Clamp(match - Math.Max(0, (budget - query.Length) / 2), 0, Math.Max(0, value.Length - budget));
        var end = Math.Min(value.Length, start + budget);
        leading = start > 0;
        trailing = end < value.Length;
        var excerpt = value[start..end];
        return (leading ? "…" : "") + excerpt + (trailing ? "…" : "");
    }
    private static DefinitionSearchDestination Workspace(string fragment) => new(DefinitionSearchDestinationKind.Workspace, fragment);
    private static DefinitionSearchDestination Composition(string fragment) => new(DefinitionSearchDestinationKind.Composition, fragment);

    private sealed record SearchEntry(string Identity, DefinitionSearchItemKind Kind, string Name,
        IReadOnlyList<string> OwnText, IReadOnlyList<string> References, string? Context, DefinitionSearchDestination Destination);
    private sealed record MatchResult(SearchEntry Entry, int Rank, DefinitionSearchMatchKind MatchKind,
        string MatchedText, string? ReferenceLabel);
}
