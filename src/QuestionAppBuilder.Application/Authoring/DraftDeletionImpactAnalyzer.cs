using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

public static class DraftDeletionImpactAnalyzer
{
    public static DraftDeletionPreview Analyze(ApplicationVersionGraph graph, DraftDeletionKind kind, int targetId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var target = DraftDeletionGraph.FindTarget(graph, kind, targetId)
            ?? throw new KeyNotFoundException($"{kind} {targetId} is not in this draft.");
        var removed = new List<DeletionImpactItem>();
        var changed = new List<DeletionImpactItem>();
        var repair = new List<DeletionImpactItem>();
        var blocked = new List<DeletionImpactItem>();
        var warnings = new List<string>();

        var category = target.Category;
        var rootElement = target.Element;
        var option = target.Option;
        var branch = kind switch
        {
            DraftDeletionKind.Category => category!.Elements.ToArray(),
            DraftDeletionKind.Element => FollowUpStructure.Branch(rootElement!, category!.Elements).ToArray(),
            DraftDeletionKind.Option => category!.Elements.Where(e => e.ParentOptionId == option!.Id)
                .SelectMany(e => FollowUpStructure.Branch(e, category.Elements)).Distinct().ToArray(),
            _ => []
        };
        var removedElements = branch.ToHashSet();
        var removedQuestions = branch.Where(e => e.Question is not null).Select(e => e.Question!).ToArray();
        var removedQuestionIds = removedQuestions.Select(q => q.Id).ToHashSet();
        var removedQuestionKeys = branch.Where(e => e.Question is not null).Select(e => e.LogicalKey).ToHashSet();
        var removedElementKeys = branch.Select(e => e.LogicalKey).ToHashSet();
        var removedCategoryKeys = kind == DraftDeletionKind.Category ? new HashSet<Guid> { category!.LogicalKey } : new HashSet<Guid>();
        var removedOptionIds = removedQuestions.SelectMany(q => q.Options).Select(o => o.Id).ToHashSet();
        var removedOptionKeys = removedQuestions.SelectMany(q => q.Options).Select(o => o.LogicalKey).ToHashSet();
        if (kind == DraftDeletionKind.Option)
        {
            removedOptionIds.Add(option!.Id);
            removedOptionKeys.Add(option.LogicalKey);
        }

        removed.Add(new(TargetTitle(target), TargetDescription(kind), Section: "Questionnaire",
            CategoryId: category?.Id, ElementId: target.Element?.Id, Key: target.LogicalKey));
        foreach (var element in branch.Where(e => kind != DraftDeletionKind.Element || !ReferenceEquals(e, rootElement)))
            removed.Add(new(element.Label, element.Question is null ? "Content block" : "Question definition and answer options",
                Section: "Questionnaire", CategoryId: category?.Id, ElementId: element.Id));
        foreach (var removedQuestion in removedQuestions)
        {
            var owner = branch.FirstOrDefault(element => ReferenceEquals(element.Question, removedQuestion));
            if (removedQuestion.Rules.Count > 0)
                removed.Add(new($"Scoring rules ({removedQuestion.Rules.Count})", $"Question scoring configuration owned by {owner?.Label ?? "a removed question"}.",
                    Section: "Questionnaire", ElementId: owner?.Id));
            foreach (var removedOption in removedQuestion.Options)
                removed.Add(new(removedOption.Label, $"Answer option in {owner?.Label ?? "a removed question"}.", Section: "Questionnaire", ElementId: owner?.Id));
        }
        var removedConditionIds = graph.VisibilityConditions
            .Where(condition => condition.Predicates.Any(predicate => removedQuestionIds.Contains(predicate.SourceQuestionId)
                || predicate.Options.Any(link => removedOptionIds.Contains(link.QuestionOptionId))))
            .Select(condition => condition.Id).ToHashSet();
        if (kind == DraftDeletionKind.Category && category!.VisibilityConditionId is int categoryConditionId)
            removedConditionIds.Add(categoryConditionId);
        foreach (var element in branch)
            if (element.VisibilityConditionId is int conditionId) removedConditionIds.Add(conditionId);
        foreach (var conditionId in removedConditionIds)
        {
            removed.Add(new($"Visibility condition {conditionId}", "Condition is removed because its source or target is deleted.", Section: "Visibility"));
            foreach (var ownerCategory in graph.Categories.Where(c => c.VisibilityConditionId == conditionId && !removedCategoryKeys.Contains(c.LogicalKey)))
                changed.Add(new(ownerCategory.Name, "Assigned visibility condition is removed; any other applicable visibility rules still apply.", Section: "Questionnaire", CategoryId: ownerCategory.Id));
            foreach (var ownerElement in graph.Categories.SelectMany(c => c.Elements).Where(e => e.VisibilityConditionId == conditionId && !removedElementKeys.Contains(e.LogicalKey)))
                changed.Add(new(ownerElement.Label, "Assigned visibility condition is removed; category, parent, and other applicable visibility rules still apply.", Section: "Questionnaire", CategoryId: ownerElement.CategoryId, ElementId: ownerElement.Id));
        }

        CompositionDefinition definition;
        try { definition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (System.Text.Json.JsonException)
        {
            warnings.Add("Saved composition configuration is malformed; deletion impact cannot be determined safely.");
            return Create();
        }
        if (definition.Groups is null || definition.Calculations is null || definition.Rules is null
            || definition.DynamicText is null || definition.DynamicText.Variables is null || definition.DynamicText.Templates is null)
        {
            warnings.Add("Saved composition configuration is incomplete; deletion impact cannot be determined safely.");
            return Create();
        }
        if (definition.Groups.Any(g => g is null || g.Rows is null || g.Rows.Any(row => row is null))
            || definition.Calculations.Any(c => c is null || c.Sources is null || c.Sources.Any(source => source is null))
            || definition.Rules.Any(rule => rule is null || rule.Conditions is null || rule.Checks is null
                || rule.Conditions.Concat(rule.Checks).Any(check => check is null || check.Left is null || check.Right is null))
            || definition.DynamicText.Variables.Any(variable => variable is null || variable.Sources is null || variable.Sources.Any(source => source is null))
            || definition.DynamicText.Templates.Any(template => template is null || template.Parts is null
                || template.Parts.Any(part => part is null || part.Kind == DynamicPartKind.Value && part.Source is null)))
        {
            warnings.Add("Saved composition configuration contains incomplete items; deletion impact cannot be determined safely.");
            return Create();
        }
        if (definition.Groups.Any(item => item.Id == Guid.Empty) || definition.Groups.Select(item => item.Id).Distinct().Count() != definition.Groups.Count
            || definition.Calculations.Any(item => item.Id == Guid.Empty || !Enum.IsDefined(item.Operation))
            || definition.Calculations.Select(item => item.Id).Distinct().Count() != definition.Calculations.Count
            || definition.Rules.Any(item => item.Id == Guid.Empty || !Enum.IsDefined(item.Kind)
                || item.Conditions.Concat(item.Checks).Any(check => !Enum.IsDefined(check.Comparison)))
            || definition.Rules.Select(item => item.Id).Distinct().Count() != definition.Rules.Count
            || definition.DynamicText.Variables.Any(item => item.Id == Guid.Empty || !Enum.IsDefined(item.Operation))
            || definition.DynamicText.Variables.Select(item => item.Id).Distinct().Count() != definition.DynamicText.Variables.Count
            || definition.DynamicText.Templates.Any(item => !Enum.IsDefined(item.Slot)
                || item.Parts.Any(part => !Enum.IsDefined(part.Kind)
                    || part.Kind == DynamicPartKind.Value && part.Source is not null && !Enum.IsDefined(part.Source.Kind)))
            || definition.DynamicText.Templates.Select(item => (item.Slot, item.OwnerKey)).Distinct().Count() != definition.DynamicText.Templates.Count
            || definition.Calculations.SelectMany(item => item.Sources).Any(item => !Enum.IsDefined(item.Kind))
            || definition.Rules.SelectMany(item => item.Conditions.Concat(item.Checks))
                .SelectMany(item => new[] { item.Left, item.Right }).Any(item => !Enum.IsDefined(item.Kind))
            || definition.DynamicText.Variables.SelectMany(item => item.Sources).Any(item => !Enum.IsDefined(item.Kind)))
        {
            warnings.Add("Saved composition configuration contains unsupported or duplicated identities; deletion impact cannot be determined safely.");
            return Create();
        }

        var removedCalculationIds = new HashSet<Guid>();
        var removedVariableIds = new HashSet<Guid>();
        if (kind == DraftDeletionKind.Category)
        {
            foreach (var group in definition.Groups.Where(group => group.CategoryKey == category!.LogicalKey))
                removed.Add(new(group.Name, "Question group owned by the deleted category.", "Groups", Key: group.Id));
            foreach (var calculation in definition.Calculations.Where(calculation => calculation.CategoryKey == category!.LogicalKey))
            {
                removedCalculationIds.Add(calculation.Id);
                removed.Add(new(calculation.Name, "Calculated field owned by the deleted category.", "Calculations", Key: calculation.Id));
            }
            foreach (var rule in definition.Rules.Where(rule => rule.CategoryKey == category!.LogicalKey))
                removed.Add(new(rule.Name, "Combined rule owned by the deleted category.", "Rules", Key: rule.Id));
            foreach (var variable in definition.DynamicText.Variables.Where(variable => variable.CategoryKey == category!.LogicalKey))
            {
                removedVariableIds.Add(variable.Id);
                removed.Add(new(variable.Name, "Dynamic variable owned by the deleted category.", "DynamicText", Key: variable.Id));
            }
        }

        var removedElementKeysForGroupRows = kind == DraftDeletionKind.Category ? removedQuestionKeys : removedElementKeys;
        foreach (var group in definition.Groups)
        {
            if (kind == DraftDeletionKind.Category && group.CategoryKey == category!.LogicalKey) continue;
            var affectedRows = group.Rows.Count(row => removedElementKeysForGroupRows.Contains(row.QuestionKey));
            var left = group.Rows.Count - affectedRows;
            var groupRemovalRuns = kind == DraftDeletionKind.Category || kind == DraftDeletionKind.Element || branch.Length > 0;
            if (left == 0 && groupRemovalRuns)
            {
                if (affectedRows > 0) changed.Add(new(group.Name, "Its remaining rows are removed.", "Groups", Key: group.Id));
                removed.Add(new(group.Name, "Question group has no rows after the deletion.", "Groups", Key: group.Id));
            }
            else if (affectedRows > 0)
                changed.Add(new(group.Name, $"{affectedRows} row(s) removed; {left} row(s) remain.", "Groups", Key: group.Id));
        }

        foreach (var template in definition.DynamicText.Templates.Where(template => removedElementKeys.Contains(template.OwnerKey)))
            removed.Add(new(TemplateName(graph, template), $"{template.Slot} template owned by the removed question/content block.", "DynamicText", Key: template.OwnerKey));

        var brokenCalculationIds = new HashSet<Guid>(removedCalculationIds);
        var calculationRepair = new Dictionary<Guid, bool>();
        foreach (var calculation in definition.Calculations.Where(calculation => !removedCalculationIds.Contains(calculation.Id)))
        {
            var direct = (calculation.Sources ?? []).Any(source => source?.Kind == SourceKind.Question && removedQuestionKeys.Contains(source.Key)
                || source?.Kind == SourceKind.OptionSelected && removedOptionKeys.Contains(source.Key));
            if (direct) { calculationRepair[calculation.Id] = true; brokenCalculationIds.Add(calculation.Id); }
        }
        var changedCalculations = true;
        while (changedCalculations)
        {
            changedCalculations = false;
            foreach (var calculation in definition.Calculations.Where(calculation => !removedCalculationIds.Contains(calculation.Id)))
            {
                if (brokenCalculationIds.Contains(calculation.Id)) continue;
                if ((calculation.Sources ?? []).Any(source => source?.Kind == SourceKind.Calculation && brokenCalculationIds.Contains(source.Key)))
                { brokenCalculationIds.Add(calculation.Id); calculationRepair[calculation.Id] = false; changedCalculations = true; }
            }
        }
        foreach (var calculation in definition.Calculations.Where(calculation => calculationRepair.ContainsKey(calculation.Id)))
            repair.Add(new(calculation.Name, calculationRepair[calculation.Id]
                ? "Calculated field directly reads a deleted question or answer option."
                : "Calculated field reads another calculated field that needs repair.", "Calculations", Key: calculation.Id));

        var brokenVariableIds = new HashSet<Guid>(removedVariableIds);
        var variableRepair = new Dictionary<Guid, bool>();
        foreach (var variable in definition.DynamicText.Variables.Where(variable => !removedVariableIds.Contains(variable.Id)))
        {
            var direct = (variable.Sources ?? []).Any(source => source?.Kind == DynamicSourceKind.Question && removedQuestionKeys.Contains(source.Key)
                || source?.Kind == DynamicSourceKind.Calculation && brokenCalculationIds.Contains(source.Key));
            if (direct) { variableRepair[variable.Id] = true; brokenVariableIds.Add(variable.Id); }
        }
        var changedVariables = true;
        while (changedVariables)
        {
            changedVariables = false;
            foreach (var variable in definition.DynamicText.Variables.Where(variable => !removedVariableIds.Contains(variable.Id)))
            {
                if (brokenVariableIds.Contains(variable.Id)) continue;
                if ((variable.Sources ?? []).Any(source => source?.Kind == DynamicSourceKind.Variable && brokenVariableIds.Contains(source.Key)))
                { brokenVariableIds.Add(variable.Id); variableRepair[variable.Id] = false; changedVariables = true; }
            }
        }
        foreach (var variable in definition.DynamicText.Variables.Where(variable => variableRepair.ContainsKey(variable.Id)))
            repair.Add(new(variable.Name, variableRepair[variable.Id]
                ? "Dynamic variable directly reads a deleted question or a calculated value that needs repair."
                : "Dynamic variable reads another dynamic value that needs repair.", "DynamicText", Key: variable.Id));
        foreach (var rule in definition.Rules.Where(rule => kind != DraftDeletionKind.Category || rule.CategoryKey != category!.LogicalKey))
        {
            var sourceMissing = (rule.Conditions ?? []).Concat(rule.Checks ?? []).Any(check => check is not null
                && (ValueSourceMissing(check.Left, removedQuestionKeys, removedOptionKeys, brokenCalculationIds)
                    || ValueSourceMissing(check.Right, removedQuestionKeys, removedOptionKeys, brokenCalculationIds)));
            var targetMissing = removedQuestionKeys.Contains(rule.TargetQuestionKey);
            if (sourceMissing || targetMissing)
                repair.Add(new(rule.Name, sourceMissing ? "Combined rule keeps a check or condition that refers to a removed value." : "Combined rule targets a removed question.", "Rules", Key: rule.Id));
            else if (kind != DraftDeletionKind.Category && removedQuestions.Length > 0
                && rule.CategoryKey == category!.LogicalKey && rule.TargetQuestionKey == Guid.Empty)
                changed.Add(new(rule.Name, "Its default target may move to the next visible question in this category.", "Rules", Key: rule.Id));
        }
        foreach (var template in definition.DynamicText.Templates.Where(template => !removedElementKeys.Contains(template.OwnerKey)))
        {
            if ((template.Parts ?? []).Any(part => part?.Kind == DynamicPartKind.Value && part.Source is { } source
                && (source.Kind == DynamicSourceKind.Question && removedQuestionKeys.Contains(source.Key)
                    || source.Kind == DynamicSourceKind.Calculation && brokenCalculationIds.Contains(source.Key)
                    || source.Kind == DynamicSourceKind.Variable && brokenVariableIds.Contains(source.Key))))
                repair.Add(new(TemplateName(graph, template), $"{template.Slot} template uses a dynamic value that needs repair after this deletion.", "DynamicText", Key: template.OwnerKey));
        }

        if (kind == DraftDeletionKind.Option && DraftDeletionGraph.IsOptionReferencedByRule(graph, option!))
            blocked.Add(new(option!.Label, "A scoring rule uses this option. Remove or change that scoring rule first.", "Questionnaire", ElementId: target.Element?.Id));

        // Keep impacts distinct by their identity and explain the category/section for repair links.
        return Create();

        DraftDeletionPreview Create() => new(graph.Version.ApplicationId, graph.Version.Id, kind, targetId, target.LogicalKey,
            graph.Version.RevisionNonce.ToByteArray(), TargetTitle(target), Deduplicate(removed), Deduplicate(changed), Deduplicate(repair), Deduplicate(blocked), warnings);
    }

    private static IReadOnlyList<DeletionImpactItem> Deduplicate(IEnumerable<DeletionImpactItem> items)
        => items.DistinctBy(item => $"{item.Name}|{item.Detail}|{item.Section}|{item.CategoryId}|{item.ElementId}|{item.Key}").ToArray();
    private static bool ValueSourceMissing(ValueSource? source, HashSet<Guid> questionKeys, HashSet<Guid> optionKeys, HashSet<Guid> calculationIds)
        => source is not null && (source.Kind == SourceKind.Question && questionKeys.Contains(source.Key)
            || source.Kind == SourceKind.OptionSelected && optionKeys.Contains(source.Key)
            || source.Kind == SourceKind.Calculation && calculationIds.Contains(source.Key));
    private static string TemplateName(ApplicationVersionGraph graph, DynamicTemplate template)
    {
        var owner = graph.Categories.SelectMany(category => category.Elements).FirstOrDefault(element => element.LogicalKey == template.OwnerKey);
        return owner is null ? template.Slot.ToString() : owner.Label;
    }
    private static string TargetTitle(DraftDeletionEntity target) => target.Kind switch
    {
        DraftDeletionKind.Category => target.Category!.Name,
        DraftDeletionKind.Element => target.Element!.Label,
        DraftDeletionKind.Option => target.Option!.Label,
        _ => "Selected item"
    };
    private static string TargetDescription(DraftDeletionKind kind) => kind switch
    {
        DraftDeletionKind.Category => "Category and all contained questions/content.",
        DraftDeletionKind.Element => "Question/content block and its follow-up branch.",
        DraftDeletionKind.Option => "Answer option and its follow-up branch.",
        _ => "Selected item."
    };
}
