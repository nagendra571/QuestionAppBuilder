using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Exceptions;

namespace QuestionAppBuilder.Application.Composition;

public static class CompositionCloner
{
    public static void CopyCategory(ApplicationVersionGraph graph, Category source, Category copy)
    {
        if (string.IsNullOrWhiteSpace(graph.Version.CompositionJson)) return;
        CompositionDefinition definition;
        try { definition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (System.Text.Json.JsonException exception)
        { throw new DefinitionValidationException($"Composition configuration cannot be copied because it is malformed: {exception.Message}"); }
        if (definition.Groups is null || definition.Calculations is null || definition.Rules is null
            || definition.DynamicText is null || definition.DynamicText.Variables is null || definition.DynamicText.Templates is null
            || definition.Groups.Any(item => item is null) || definition.Calculations.Any(item => item is null)
            || definition.Rules.Any(item => item is null) || definition.DynamicText.Variables.Any(item => item is null)
            || definition.DynamicText.Templates.Any(item => item is null))
            throw new DefinitionValidationException("Composition configuration cannot be copied because it contains an incomplete item.");
        if (definition.Calculations.Any(item => item.Id == Guid.Empty)
            || definition.Calculations.Select(item => item.Id).Distinct().Count() != definition.Calculations.Count
            || definition.Groups.Any(item => item.Id == Guid.Empty)
            || definition.Groups.Select(item => item.Id).Distinct().Count() != definition.Groups.Count
            || definition.Rules.Any(item => item.Id == Guid.Empty)
            || definition.Rules.Select(item => item.Id).Distinct().Count() != definition.Rules.Count
            || definition.DynamicText.Variables.Any(item => item.Id == Guid.Empty)
            || definition.DynamicText.Variables.Select(item => item.Id).Distinct().Count() != definition.DynamicText.Variables.Count
            || definition.DynamicText.Templates.Any(item => !Enum.IsDefined(item.Slot))
            || definition.DynamicText.Templates.Select(item => (item.Slot, item.OwnerKey)).Distinct().Count() != definition.DynamicText.Templates.Count)
            throw new DefinitionValidationException("Composition configuration cannot be copied because its identifiers are missing or duplicated.");
        var clones = CompositionDefinition.Read(definition.ToJson());
        var original = FollowUpStructure.Ordered(source.Elements);
        var questionMap = original.Zip(copy.Elements).ToDictionary(p => p.First.LogicalKey, p => p.Second.LogicalKey);
        var optionMap = original.Zip(copy.Elements).Where(p => p.First.Question is not null)
            .SelectMany(p => p.First.Question!.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).Zip(p.Second.Question!.Options))
            .ToDictionary(p => p.First.LogicalKey, p => p.Second.LogicalKey);
        var calculations = clones.Calculations.Where(c => c.CategoryKey == source.LogicalKey).ToArray();
        var calculationMap = calculations.ToDictionary(c => c.Id, _ => Guid.NewGuid());
        var dynamicVariables = clones.DynamicText.Variables.Where(v => v.CategoryKey == source.LogicalKey).ToArray();
        var dynamicVariableMap = dynamicVariables.ToDictionary(v => v.Id, _ => Guid.NewGuid());
        var questionKeys = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null)
            .Select(e => e.LogicalKey).ToHashSet();
        var optionKeys = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null)
            .SelectMany(e => e.Question!.Options).Select(o => o.LogicalKey).ToHashSet();
        var calculationKeys = definition.Calculations.Select(c => c.Id).ToHashSet();
        var dynamicVariableKeys = definition.DynamicText.Variables.Select(v => v.Id).ToHashSet();
        void Remap(ValueSource value)
        {
            var valid = value.Kind switch
            {
                SourceKind.Constant => true,
                SourceKind.Question => questionKeys.Contains(value.Key),
                SourceKind.OptionSelected => optionKeys.Contains(value.Key),
                SourceKind.Calculation => calculationKeys.Contains(value.Key),
                _ => false
            };
            if (!valid) throw new DefinitionValidationException("Composition configuration cannot be copied because it references a missing source.");
            var map = value.Kind switch
            {
                SourceKind.Question => questionMap,
                SourceKind.OptionSelected => optionMap,
                SourceKind.Calculation => calculationMap,
                _ => null
            };
            if (map is not null && map.TryGetValue(value.Key, out var key)) value.Key = key;
        }
        void RemapDynamic(DynamicSource value)
        {
            var valid = value.Kind switch
            {
                DynamicSourceKind.Text => value.Key == Guid.Empty && value.Text is not null && value.Number is null && value.Date is null,
                DynamicSourceKind.Number => value.Key == Guid.Empty && value.Text is null && value.Number is not null && value.Date is null,
                DynamicSourceKind.Date => value.Key == Guid.Empty && value.Text is null && value.Number is null && value.Date is not null,
                DynamicSourceKind.Today => value.Key == Guid.Empty && value.Text is null && value.Number is null && value.Date is null,
                DynamicSourceKind.Question => value.Key != Guid.Empty && value.Text is null && value.Number is null && value.Date is null && questionKeys.Contains(value.Key),
                DynamicSourceKind.Calculation => value.Key != Guid.Empty && value.Text is null && value.Number is null && value.Date is null && calculationKeys.Contains(value.Key),
                DynamicSourceKind.Variable => value.Key != Guid.Empty && value.Text is null && value.Number is null && value.Date is null && dynamicVariableKeys.Contains(value.Key),
                _ => false
            };
            if (!valid) throw new DefinitionValidationException("Dynamic text configuration cannot be copied because it references an invalid or missing source.");
            if (value.Kind == DynamicSourceKind.Question && questionMap.TryGetValue(value.Key, out var question)) value.Key = question;
            if (value.Kind == DynamicSourceKind.Calculation && calculationMap.TryGetValue(value.Key, out var calculation)) value.Key = calculation;
            if (value.Kind == DynamicSourceKind.Variable && dynamicVariableMap.TryGetValue(value.Key, out var variable)) value.Key = variable;
        }
        foreach (var group in clones.Groups.Where(g => g.CategoryKey == source.LogicalKey))
        {
            group.Id = Guid.NewGuid(); group.CategoryKey = copy.LogicalKey;
            if (group.Rows is null || group.Rows.Any(row => row is null || !questionMap.ContainsKey(row.QuestionKey)))
                throw new DefinitionValidationException("A question group in the category being copied references a missing or out-of-category question.");
            foreach (var row in group.Rows) row.QuestionKey = questionMap[row.QuestionKey];
            definition.Groups.Add(group);
        }
        foreach (var calculation in calculations)
        {
            if (calculation.Sources is null || calculation.Sources.Any(source => source is null))
                throw new DefinitionValidationException("A calculation in the category being copied has incomplete sources.");
            calculation.Id = calculationMap[calculation.Id]; calculation.CategoryKey = copy.LogicalKey;
            foreach (var value in calculation.Sources) Remap(value);
            definition.Calculations.Add(calculation);
        }
        foreach (var rule in clones.Rules.Where(r => r.CategoryKey == source.LogicalKey))
        {
            if (rule.Conditions is null || rule.Checks is null || rule.Conditions.Concat(rule.Checks)
                .Any(check => check is null || check.Left is null || check.Right is null))
                throw new DefinitionValidationException("A rule in the category being copied has incomplete checks.");
            if (rule.TargetQuestionKey != Guid.Empty && !questionMap.ContainsKey(rule.TargetQuestionKey))
                throw new DefinitionValidationException("A rule in the category being copied targets a missing or out-of-category question.");
            rule.Id = Guid.NewGuid(); rule.CategoryKey = copy.LogicalKey;
            if (questionMap.TryGetValue(rule.TargetQuestionKey, out var target)) rule.TargetQuestionKey = target;
            foreach (var check in rule.Conditions.Concat(rule.Checks)) { Remap(check.Left); Remap(check.Right); }
            definition.Rules.Add(rule);
        }
        foreach (var variable in dynamicVariables)
        {
            if (!Enum.IsDefined(variable.Operation) || variable.Sources is null || variable.Sources.Any(item => item is null))
                throw new DefinitionValidationException("A dynamic variable in the category being copied has incomplete or unsupported sources.");
            variable.Id = dynamicVariableMap[variable.Id];
            variable.CategoryKey = copy.LogicalKey;
            foreach (var value in variable.Sources) RemapDynamic(value);
            definition.DynamicText.Variables.Add(variable);
        }
        foreach (var template in clones.DynamicText.Templates.Where(t => questionMap.ContainsKey(t.OwnerKey)))
        {
            if (!Enum.IsDefined(template.Slot) || template.Parts is null || template.Parts.Any(part => part is null))
                throw new DefinitionValidationException("A dynamic template in the category being copied is incomplete or unsupported.");
            foreach (var part in template.Parts)
            {
                if (!Enum.IsDefined(part.Kind)) throw new DefinitionValidationException("A dynamic template contains an unsupported part.");
                if (part.Kind == DynamicPartKind.Value)
                {
                    if (part.Source is null) throw new DefinitionValidationException("A dynamic template contains an incomplete source.");
                    RemapDynamic(part.Source);
                }
                else if (part.Source is not null) throw new DefinitionValidationException("A dynamic literal cannot contain a source.");
            }
            template.OwnerKey = questionMap[template.OwnerKey];
            definition.DynamicText.Templates.Add(template);
        }
        graph.Version.CompositionJson = definition.ToJson();
    }

    public static void CopyElements(ApplicationVersionGraph graph, IReadOnlyList<Element> sources, IReadOnlyList<Element> copies)
    {
        if (sources.Count != copies.Count) throw new ArgumentException("Source and copy elements must have matching counts.");
        if (string.IsNullOrWhiteSpace(graph.Version.CompositionJson) || sources.Count == 0) return;
        var definition = ReadForEdit(graph.Version.CompositionJson);
        var elementMap = sources.Zip(copies).ToDictionary(pair => pair.First.LogicalKey, pair => pair.Second.LogicalKey);
        var templates = definition.DynamicText.Templates.Where(template => elementMap.ContainsKey(template.OwnerKey)).ToArray();
        if (templates.Length == 0) return;

        if (definition.DynamicText.Variables.Any(variable => variable.Id == Guid.Empty)
            || definition.DynamicText.Variables.Select(variable => variable.Id).Distinct().Count() != definition.DynamicText.Variables.Count
            || definition.DynamicText.Templates.Select(template => (template.Slot, template.OwnerKey)).Distinct().Count() != definition.DynamicText.Templates.Count)
            throw new DefinitionValidationException("Dynamic text configuration cannot be copied because its identifiers or slots are duplicated.");
        var variables = definition.DynamicText.Variables.ToDictionary(variable => variable.Id);
        var variableIdsToClone = new HashSet<Guid>();
        var pending = new Queue<Guid>(templates.SelectMany(template => template.Parts ?? [])
            .Where(part => part is not null && part.Kind == DynamicPartKind.Value && part.Source?.Kind == DynamicSourceKind.Variable)
            .Select(part => part.Source!.Key));
        while (pending.Count != 0)
        {
            var id = pending.Dequeue();
            if (!variables.TryGetValue(id, out var variable))
                throw new DefinitionValidationException("A dynamic template in the element being copied references a missing variable.");
            if (!variableIdsToClone.Add(id)) continue;
            if (variable.Sources is null || variable.Sources.Any(source => source is null))
                throw new DefinitionValidationException("A dynamic variable used by the element being copied has incomplete sources.");
            foreach (var source in variable.Sources.Where(source => source.Kind == DynamicSourceKind.Variable)) pending.Enqueue(source.Key);
        }
        var variableMap = variableIdsToClone.ToDictionary(id => id, _ => Guid.NewGuid());
        foreach (var variableId in variableIdsToClone)
        {
            var clone = JsonClone(variables[variableId]);
            clone.Id = variableMap[variableId];
            foreach (var source in clone.Sources)
            {
                if (!Enum.IsDefined(source.Kind)) throw new DefinitionValidationException("A dynamic variable uses an unsupported source.");
                if (source.Kind == DynamicSourceKind.Question && elementMap.TryGetValue(source.Key, out var question)) source.Key = question;
                if (source.Kind == DynamicSourceKind.Variable)
                {
                    if (!variableMap.TryGetValue(source.Key, out var variable)) throw new DefinitionValidationException("A dynamic variable references an invalid source.");
                    source.Key = variable;
                }
            }
            definition.DynamicText.Variables.Add(clone);
        }
        foreach (var template in templates)
        {
            if (template.Parts is null || template.Parts.Any(part => part is null))
                throw new DefinitionValidationException("A dynamic template in the element being copied has incomplete parts.");
            var clone = JsonClone(template);
            clone.OwnerKey = elementMap[template.OwnerKey];
            foreach (var part in clone.Parts)
            {
                if (!Enum.IsDefined(part.Kind)) throw new DefinitionValidationException("A dynamic template uses an unsupported part.");
                if (part.Kind != DynamicPartKind.Value) continue;
                if (part.Source is null || !Enum.IsDefined(part.Source.Kind)) throw new DefinitionValidationException("A dynamic template has an incomplete source.");
                if (part.Source.Kind == DynamicSourceKind.Question && elementMap.TryGetValue(part.Source.Key, out var question)) part.Source.Key = question;
                if (part.Source.Kind == DynamicSourceKind.Variable && variableMap.TryGetValue(part.Source.Key, out var variable)) part.Source.Key = variable;
            }
            definition.DynamicText.Templates.Add(clone);
        }
        graph.Version.CompositionJson = definition.ToJson();
    }

    public static void RemoveCategory(ApplicationVersionGraph graph, Guid categoryKey, IReadOnlySet<Guid> questionKeys)
    {
        if (string.IsNullOrWhiteSpace(graph.Version.CompositionJson)) return;
        var definition = ReadForEdit(graph.Version.CompositionJson);
        definition.Groups.RemoveAll(group => group.CategoryKey == categoryKey);
        foreach (var group in definition.Groups.ToArray())
        {
            group.Rows.RemoveAll(row => questionKeys.Contains(row.QuestionKey));
            if (group.Rows.Count == 0) definition.Groups.Remove(group);
        }
        definition.Calculations.RemoveAll(calculation => calculation.CategoryKey == categoryKey);
        definition.Rules.RemoveAll(rule => rule.CategoryKey == categoryKey);
        definition.DynamicText.Variables.RemoveAll(variable => variable.CategoryKey == categoryKey);
        var elementKeys = graph.Categories.FirstOrDefault(category => category.LogicalKey == categoryKey)?.Elements
            .Select(element => element.LogicalKey).ToHashSet() ?? new HashSet<Guid>();
        elementKeys.UnionWith(questionKeys);
        definition.DynamicText.Templates.RemoveAll(template => elementKeys.Contains(template.OwnerKey));
        graph.Version.CompositionJson = definition.ToJson();
    }

    public static void RemoveQuestions(ApplicationVersionGraph graph, IReadOnlySet<Guid> questionKeys) => RemoveElements(graph, questionKeys);

    public static void RemoveElements(ApplicationVersionGraph graph, IReadOnlySet<Guid> elementKeys)
    {
        if (elementKeys.Count == 0 || string.IsNullOrWhiteSpace(graph.Version.CompositionJson)) return;
        var definition = ReadForEdit(graph.Version.CompositionJson);
        foreach (var group in definition.Groups.ToArray())
        {
            group.Rows.RemoveAll(row => elementKeys.Contains(row.QuestionKey));
            if (group.Rows.Count == 0) definition.Groups.Remove(group);
        }
        definition.DynamicText.Templates.RemoveAll(template => elementKeys.Contains(template.OwnerKey));
        graph.Version.CompositionJson = definition.ToJson();
    }

    private static T JsonClone<T>(T value) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value))!;

    private static CompositionDefinition ReadForEdit(string json)
    {
        try
        {
            var definition = CompositionDefinition.Read(json);
            if (definition.Groups is null || definition.Calculations is null || definition.Rules is null
                || definition.DynamicText is null || definition.DynamicText.Variables is null || definition.DynamicText.Templates is null
                || definition.Groups.Any(group => group is null || group.Rows is null || group.Rows.Any(row => row is null))
                || definition.Calculations.Any(calculation => calculation is null)
                || definition.Rules.Any(rule => rule is null) || definition.DynamicText.Variables.Any(variable => variable is null)
                || definition.DynamicText.Templates.Any(template => template is null))
                throw new DefinitionValidationException("Composition configuration cannot be updated because it contains an incomplete item.");
            return definition;
        }
        catch (System.Text.Json.JsonException exception)
        { throw new DefinitionValidationException($"Composition configuration cannot be updated because it is malformed: {exception.Message}"); }
    }
}
