using System.Text.Json;
using System.Text.Json.Serialization;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyRelation { UsedBy, Owned, DependsOn, PotentialTarget }

public sealed record DefinitionDependencyItem(
    DependencyRelation Relation,
    string Kind,
    string Name,
    string Detail,
    bool IsIndirect = false,
    Guid? Key = null,
    int? ElementId = null,
    string? CompositionSection = null,
    int? CategoryId = null,
    DynamicTextSlot? TemplateSlot = null);

public sealed record DefinitionDependencyReport(
    IReadOnlyList<DefinitionDependencyItem> Items,
    IReadOnlyList<string> Warnings,
    bool Complete);

/// <summary>Inspects saved authoring references without evaluating visibility or response values.</summary>
public static class DefinitionDependencyInspector
{
    public static DefinitionDependencyReport Inspect(ApplicationVersionGraph graph, Guid elementKey)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var items = new Dictionary<string, DefinitionDependencyItem>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        void Warn(string message) { complete = false; warnings.Add(message); }
        void Add(DefinitionDependencyItem item)
        {
            var id = $"{item.Relation}|{item.Kind}|{item.Key}|{item.ElementId}|{item.CategoryId}|{item.CompositionSection}|{item.TemplateSlot}|{item.Name}";
            if (items.TryGetValue(id, out var old))
            {
                if (old.IsIndirect && !item.IsIndirect) items[id] = old with { IsIndirect = false };
            }
            else items[id] = item;
        }

        var categories = graph.Categories ?? Array.Empty<Category>();
        if (graph.Categories is null) Warn("Some questionnaire configuration could not be inspected.");
        if (categories.Any(category => category is null)) Warn("Some questionnaire configuration could not be inspected.");
        categories = categories.Where(category => category is not null).ToArray();
        foreach (var category in categories)
            if (category is null || category.Elements is null) Warn("Some questionnaire configuration could not be inspected.");
        var elements = categories.Where(c => c is not null).SelectMany(c => c.Elements ?? []).Where(e => e is not null).ToArray();
        var selected = elements.FirstOrDefault(e => e.LogicalKey == elementKey);
        if (selected is null) return new([], ["The selected item is not in this saved draft."], false);
        var selectedCategory = categories.FirstOrDefault(c => c.Elements?.Any(e => e?.LogicalKey == selected.LogicalKey) == true);
        var selectedQuestion = selected.Question;
        if (selectedQuestion is not null && selectedQuestion.Options is null) Warn("Some answer options could not be inspected.");
        if (selectedQuestion?.Options?.Any(option => option is null) == true) Warn("Some answer options could not be inspected.");
        var selectedOptions = selectedQuestion?.Options?.Where(o => o is not null).ToArray() ?? [];

        // Follow-up ownership and question scoring configuration are direct relations.
        if (selectedQuestion is not null)
        {
            var optionIds = selectedOptions.Select(o => o.Id).ToHashSet();
            foreach (var child in elements.Where(e => e.ParentOptionId is int parent && optionIds.Contains(parent)))
                Add(new(DependencyRelation.UsedBy, "Follow-up question", Name(child.Label, "Unnamed question"),
                    $"Appears when an answer option in {Name(selected.Label, "unnamed question")} is selected.", ElementId: child.Id));
            if (selectedQuestion.Rules is null) Warn("Some question scoring rules could not be inspected.");
            else if (selectedQuestion.Rules.Count > 0)
                Add(new(DependencyRelation.Owned, "Question scoring", $"Scoring rules ({selectedQuestion.Rules.Count})", "These rules award points for this question's answer.", Key: selected.LogicalKey));
        }
        if (selected.ParentOptionId is int parentOptionId)
        {
            var parent = elements.FirstOrDefault(e => e.Question?.Options?.Any(o => o.Id == parentOptionId) == true);
            if (parent is not null)
                Add(new(DependencyRelation.DependsOn, "Follow-up parent", Name(parent.Label, "Unnamed question"),
                    $"This follow-up appears when one of {Name(parent.Label, "the parent question")}'s options is selected.", ElementId: parent.Id));
            else Warn("The follow-up parent reference could not be resolved.");
        }

        // Visibility conditions can be assigned to several targets and may also be unassigned.
        if (selectedQuestion is not null)
        {
            var conditionIds = new HashSet<int>();
            var relevantOptionIds = selectedOptions.Select(o => o.Id).ToHashSet();
            if (graph.VisibilityConditions is null) Warn("Some visibility conditions could not be inspected.");
            foreach (var condition in graph.VisibilityConditions ?? [])
            {
                if (condition is null || condition.Predicates is null) { Warn("Some visibility condition predicates could not be inspected."); continue; }
                foreach (var predicate in condition.Predicates)
            {
                if (predicate is null) { Warn("Some configuration could not be inspected."); continue; }
                if (predicate.Options is null || predicate.Options.Any(option => option is null)) Warn("Some visibility condition options could not be inspected.");
                var referencesQuestion = predicate.SourceQuestionId == selectedQuestion.Id;
                var referencesOption = (predicate.Options ?? []).Any(o => o is not null && relevantOptionIds.Contains(o.QuestionOptionId));
                if (referencesQuestion || referencesOption) conditionIds.Add(condition!.Id);
            }
            }
            foreach (var conditionId in conditionIds)
            {
                var categoryTargets = categories.Where(c => c.VisibilityConditionId == conditionId).ToArray();
                var elementTargets = elements.Where(e => e.VisibilityConditionId == conditionId).ToArray();
                foreach (var target in categoryTargets)
                    Add(new(DependencyRelation.UsedBy, "Category visibility", Name(target.Name, "Unnamed category"),
                        "Its visibility condition refers to this question.", CategoryId: target.Id));
                foreach (var target in elementTargets)
                    Add(new(DependencyRelation.UsedBy, target.Question is null ? "Content visibility" : "Question visibility", Name(target.Label, "Unnamed element"),
                        "Its visibility condition refers to this question.", ElementId: target.Id));
                if (categoryTargets.Length == 0 && elementTargets.Length == 0)
                    Add(new(DependencyRelation.UsedBy, "Visibility condition", $"Condition {conditionId}",
                        "An unassigned condition refers to this question.", CompositionSection: "visibility"));
            }
        }

        CompositionDefinition composition;
        try { composition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (JsonException) { Warn("Some configuration could not be inspected because its saved composition data is malformed."); return Result(); }

        if (composition.Groups is null || composition.Calculations is null || composition.Rules is null || composition.DynamicText is null)
        {
            Warn("Some configuration could not be inspected.");
            return Result();
        }

        if (composition.Groups.Any(group => group is null || group.Rows is null || group.Rows.Any(row => row is null)))
            Warn("Some question groups could not be inspected.");
        if (selectedQuestion is not null)
        {
            foreach (var group in composition.Groups.Where(g => g is not null && (g.Rows ?? []).Any(r => r?.QuestionKey == selected.LogicalKey)))
                Add(new(DependencyRelation.UsedBy, "Question group", Name(group!.Name, "Unnamed group"),
                    "Includes this question as a row.", Key: group.Id, CompositionSection: "Groups"));
        }

        // Build the cycle-safe transitive closure of calculations that depend on this question.
        var calculations = new Dictionary<Guid, CalculatedField>();
        foreach (var calculation in composition.Calculations)
        {
            if (calculation is null) { Warn("Some calculations could not be inspected."); continue; }
            if (calculation.Sources is null || calculation.Sources.Any(source => source is null)) Warn("Some calculation sources could not be inspected.");
            if (!calculations.TryAdd(calculation.Id, calculation)) Warn("Duplicate calculation references were found; some entries may be incomplete.");
        }
        var directCalculations = new HashSet<Guid>();
        if (selectedQuestion is not null)
            foreach (var calculation in calculations.Values.Where(c => HasQuestionSource(c.Sources, selected.LogicalKey, selectedOptions))) directCalculations.Add(calculation.Id);
        var reachedCalculations = new HashSet<Guid>(directCalculations);
        var queue = new Queue<Guid>(directCalculations);
        while (queue.TryDequeue(out var sourceId))
        {
            foreach (var downstream in calculations.Values.Where(c => (c.Sources ?? []).Any(s => s?.Kind == SourceKind.Calculation && s.Key == sourceId)))
                if (reachedCalculations.Add(downstream.Id)) queue.Enqueue(downstream.Id);
        }
        if (HasCalculationCycle(calculations)) Warn("A calculation cycle was found; known references are shown where possible.");
        foreach (var id in reachedCalculations)
        {
            var calc = calculations[id];
            Add(new(DependencyRelation.UsedBy, "Calculated field", Name(calc.Name, "Unnamed calculation"),
                directCalculations.Contains(id) ? "Reads this question or one of its selected options." : "Reads another calculation that depends on this question.",
                !directCalculations.Contains(id), calc.Id, CompositionSection: "Calculations"));
        }

        if (composition.Rules.Any(rule => rule is null)) Warn("Some combined rules could not be inspected.");
        var rules = composition.Rules.Where(r => r is not null).ToArray();
        if (selectedQuestion is not null)
        {
            foreach (var rule in rules)
            {
                if (rule.Conditions is null || rule.Checks is null) { Warn("Some combined rules could not be inspected."); continue; }
                if (rule.Conditions.Concat(rule.Checks).Any(check => check is null || check.Left is null || check.Right is null))
                    Warn("Some combined rule sources could not be inspected.");
                var direct = rule.Conditions.Concat(rule.Checks).Any(c => c is not null &&
                    (HasQuestionSource([c.Left, c.Right], selected.LogicalKey, selectedOptions)));
                var throughCalculation = rule.Conditions.Concat(rule.Checks).Any(c => c is not null &&
                    HasCalculationSource([c.Left, c.Right], reachedCalculations));
                var explicitTarget = rule.TargetQuestionKey == selected.LogicalKey;
                if (direct || throughCalculation || explicitTarget)
                {
                    var detail = explicitTarget
                        ? direct ? "Targets this question and reads it in its checks." : throughCalculation ? "Targets this question and reads a calculation that depends on it." : "Targets this question when it is visible."
                        : direct ? "Reads this question in its conditions or checks." : "Reads a calculation that depends on this question.";
                    Add(new(DependencyRelation.UsedBy, rule.Kind == RulePurpose.Validation ? "Validation rule" : "Scoring rule",
                        Name(rule.Name, "Unnamed rule"), detail,
                        !direct && !explicitTarget, rule.Id, CompositionSection: "Rules"));
                }
                if (rule.TargetQuestionKey == Guid.Empty && selectedCategory is not null && rule.CategoryKey == selectedCategory.LogicalKey)
                    Add(new(DependencyRelation.PotentialTarget, "Default rule target", Name(rule.Name, "Unnamed rule"),
                        "May attach to this question when it is the first visible question in the category.", Key: rule.Id, CompositionSection: "Rules"));
            }
        }

        var dynamic = composition.DynamicText;
        if (dynamic.Variables is null) Warn("Some dynamic variables could not be inspected.");
        if (dynamic.Templates is null) Warn("Some dynamic text templates could not be inspected.");
        var variables = new Dictionary<Guid, DynamicVariable>();
        foreach (var variable in dynamic.Variables ?? [])
        {
            if (variable is null) { Warn("Some dynamic variables could not be inspected."); continue; }
            if (variable.Sources is null || variable.Sources.Any(source => source is null)) Warn("Some dynamic variable sources could not be inspected.");
            if (!variables.TryAdd(variable.Id, variable)) Warn("Duplicate dynamic variable references were found; some entries may be incomplete.");
        }
        var directVariables = new HashSet<Guid>();
        if (selectedQuestion is not null)
            foreach (var variable in variables.Values.Where(v => HasDynamicQuestionSource(v.Sources, selected.LogicalKey))) directVariables.Add(variable.Id);
        var reachedVariables = new HashSet<Guid>(directVariables);
        foreach (var variable in variables.Values.Where(v => HasDynamicCalculationSource(v.Sources, reachedCalculations))) reachedVariables.Add(variable.Id);
        var variableQueue = new Queue<Guid>(reachedVariables);
        while (variableQueue.TryDequeue(out var variableId))
        {
            foreach (var downstream in variables.Values.Where(v => (v.Sources ?? []).Any(s => s?.Kind == DynamicSourceKind.Variable && s.Key == variableId)))
                if (reachedVariables.Add(downstream.Id)) variableQueue.Enqueue(downstream.Id);
        }
        if (HasVariableCycle(variables)) Warn("A dynamic variable cycle was found; known references are shown where possible.");
        foreach (var id in reachedVariables)
            Add(new(DependencyRelation.UsedBy, "Dynamic variable", Name(variables[id].Name, "Unnamed variable"),
                directVariables.Contains(id) ? "Reads this question directly." : "Reads a variable or calculation that depends on this question.",
                !directVariables.Contains(id), id, CompositionSection: "DynamicText"));

        foreach (var template in dynamic.Templates ?? [])
        {
            if (template is null) { Warn("Some dynamic text templates could not be inspected."); continue; }
            if (template.Parts is null) { Warn("Some dynamic text template parts could not be inspected."); continue; }
            if (template.Parts.Any(part => part is null || part.Kind == DynamicPartKind.Value && part.Source is null))
                Warn("Some dynamic text template sources could not be inspected.");
            var owned = template.OwnerKey == selected.LogicalKey;
            var direct = selectedQuestion is not null && (template.Parts ?? []).Any(p => p?.Kind == DynamicPartKind.Value && p.Source?.Kind == DynamicSourceKind.Question && p.Source.Key == selected.LogicalKey);
            var indirect = (template.Parts ?? []).Any(p => p?.Kind == DynamicPartKind.Value &&
                (p.Source?.Kind == DynamicSourceKind.Calculation && reachedCalculations.Contains(p.Source.Key)
                 || p.Source?.Kind == DynamicSourceKind.Variable && reachedVariables.Contains(p.Source.Key)));
            if (!owned && !direct && !indirect) continue;
            var owner = elements.FirstOrDefault(e => e.LogicalKey == template.OwnerKey);
            var templateName = owner is not null ? Name(owner.Label, "Unnamed element") : template.Slot switch
            {
                DynamicTextSlot.ReviewSummary => "Review summary",
                DynamicTextSlot.ConfirmationText => "Confirmation text",
                _ => "Dynamic text template"
            };
            var slotName = template.Slot switch { DynamicTextSlot.ElementLabel => "dynamic label", DynamicTextSlot.ElementHelpText => "dynamic help text", _ => "dynamic text" };
            var slotKind = template.Slot switch { DynamicTextSlot.ElementLabel => "Dynamic label", DynamicTextSlot.ElementHelpText => "Dynamic help text", _ => "Dynamic text" };
            Add(new(owned ? DependencyRelation.Owned : DependencyRelation.UsedBy,
                owned ? slotKind : $"{slotKind} reference", owned ? $"{slotName} for {templateName}" : templateName,
                owned ? $"This item has a {slotName} template."
                    : direct ? "Uses this question directly." : "Uses a variable or calculation that depends on this question.", !direct && indirect, Key: template.OwnerKey == Guid.Empty ? null : template.OwnerKey,
                ElementId: owner?.Id, CompositionSection: "DynamicText", TemplateSlot: template.Slot));
        }

        return Result();

        DefinitionDependencyReport Result() => new(items.Values.OrderBy(i => i.Relation).ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal).ToArray(), warnings.OrderBy(w => w, StringComparer.Ordinal).ToArray(), complete);
    }

    private static bool HasQuestionSource(IEnumerable<ValueSource?>? sources, Guid key, IReadOnlyCollection<QuestionOption> options)
    {
        if (sources is null) return false;
        var optionKeys = options.Select(o => o.LogicalKey).ToHashSet();
        return sources.Any(s => s is not null && (s.Kind == SourceKind.Question && s.Key == key || s.Kind == SourceKind.OptionSelected && optionKeys.Contains(s.Key)));
    }
    private static bool HasCalculationSource(IEnumerable<ValueSource?>? sources, HashSet<Guid> ids) => sources?.Any(s => s?.Kind == SourceKind.Calculation && ids.Contains(s.Key)) == true;
    private static bool HasDynamicQuestionSource(IEnumerable<DynamicSource?>? sources, Guid key) => sources?.Any(s => s?.Kind == DynamicSourceKind.Question && s.Key == key) == true;
    private static bool HasDynamicCalculationSource(IEnumerable<DynamicSource?>? sources, HashSet<Guid> ids) => sources?.Any(s => s?.Kind == DynamicSourceKind.Calculation && ids.Contains(s.Key)) == true;
    private static string Name(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool HasCalculationCycle(Dictionary<Guid, CalculatedField> fields)
    {
        var done = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (done.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            var cycle = (fields[id].Sources ?? []).Where(s => s?.Kind == SourceKind.Calculation && fields.ContainsKey(s.Key)).Any(s => Visit(s!.Key));
            visiting.Remove(id); done.Add(id); return cycle;
        }
        return fields.Keys.Any(Visit);
    }
    private static bool HasVariableCycle(Dictionary<Guid, DynamicVariable> fields)
    {
        var done = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (done.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            var cycle = (fields[id].Sources ?? []).Where(s => s?.Kind == DynamicSourceKind.Variable && fields.ContainsKey(s.Key)).Any(s => Visit(s!.Key));
            visiting.Remove(id); done.Add(id); return cycle;
        }
        return fields.Keys.Any(Visit);
    }
}
