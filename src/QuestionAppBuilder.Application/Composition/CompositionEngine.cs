using System.Text.Json;
using System.Text.Json.Serialization;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain;

namespace QuestionAppBuilder.Application.Composition;

public sealed record CalculatedValue(Guid Id, string Name, Guid CategoryKey, decimal? Value, bool Percentage);
public sealed record CompositionAward(Guid Id, string Name, Guid CategoryKey, int Points);
public sealed record CompositionLiveAnswerError(Guid RuleId, int QuestionId, string Message);
public sealed record CompositionInputEffect(SourceKind Kind, Guid Key, string Code, decimal? UsedValue);
public sealed record CompositionValueRead(decimal? Value, IReadOnlyList<CompositionInputEffect> Effects);
public sealed record CompositionCheckEvaluation(int Index, CompositionValueRead Left, CompositionValueRead Right, bool Matches);
public sealed record CompositionValidationFailure(Guid RuleId, int TargetQuestionId, bool ShowWhileAnswering, bool AnyCheck,
    IReadOnlyList<CompositionCheckEvaluation> Checks);
public sealed record CombinedScoreRuleDiagnostic(Guid RuleId, int? TargetQuestionId, string Outcome, int AwardedPoints,
    Guid? SupersedingRuleId, IReadOnlyList<CompositionCheckEvaluation> Conditions, IReadOnlyList<CompositionCheckEvaluation> Checks);
public sealed record DynamicValue(DynamicValueKind Kind, DynamicValueState State, string? Text = null,
    decimal? Number = null, DateOnly? Date = null, IReadOnlyList<string>? Items = null);
public sealed record DynamicVariableResult(Guid Id, string Name, DynamicValue Value);
public sealed record ResolvedDynamicText(DynamicTextSlot Slot, Guid OwnerKey, string Text)
{
    public bool IsHidden { get; init; }
}
public sealed record DynamicTextResult(DateTimeOffset EvaluatedAtUtc, string TimeZoneId, DateOnly ReferenceDate,
    IReadOnlyList<DynamicVariableResult> Values, IReadOnlyList<ResolvedDynamicText> Texts,
    IReadOnlyList<string> DefinitionErrors);
public sealed record CompositionResult(IReadOnlyList<CalculatedValue> Values, IReadOnlyList<CompositionAward> Awards,
    IReadOnlyList<string> DefinitionErrors, IReadOnlyList<(int QuestionId, string Message)> AnswerErrors)
{
    public IReadOnlyList<CompositionLiveAnswerError> LiveAnswerErrors { get; init; } = [];
    [JsonIgnore]
    public IReadOnlyList<CompositionValidationFailure>? FailedValidationRules { get; init; }
    [JsonIgnore]
    public IReadOnlyList<CombinedScoreRuleDiagnostic>? CombinedScoreRules { get; init; }
    public DynamicTextResult? DynamicText { get; init; }
    public IReadOnlyList<int>? VisibleCategoryIds { get; init; }
    /// <summary>Element visibility frozen at submission; null preserves legacy snapshots, while an empty list means none were visible.</summary>
    public IReadOnlyList<int>? VisibleElementIds { get; init; }
}

public static class CompositionEngine
{
    public static CompositionResult Evaluate(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers,
        IReadOnlySet<int> visible, IReadOnlySet<int>? visibleCategoryIds = null)
    {
        var errors = new List<string>();
        var issues = new List<(int, string)>();
        var liveIssues = new List<CompositionLiveAnswerError>();
        var failedValidationRules = new List<CompositionValidationFailure>();
        var values = new Dictionary<Guid, decimal?>();
        var calculationEffects = new Dictionary<Guid, IReadOnlyList<CompositionInputEffect>>();
        var results = new List<CalculatedValue>();
        var awards = new List<CompositionAward>();
        CompositionDefinition definition;
        try { definition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (JsonException) { return new(results, awards, ["Groups and calculations configuration is malformed."], issues); }
        if (definition.Groups is null || definition.Calculations is null || definition.Rules is null
            || definition.Groups.Count > 100 || definition.Calculations.Count > 200 || definition.Rules.Count > 500
            || definition.Groups.Any(g => g is null) || definition.Calculations.Any(c => c is null) || definition.Rules.Any(r => r is null))
            return new(results, awards, ["Configuration exceeds supported limits or contains missing lists."], issues);
        var elements = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).ToArray();
        var questions = elements.GroupBy(e => e.LogicalKey).ToDictionary(g => g.Key, g => g.First());
        var orderedCategories = graph.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToArray();
        var categoryPositions = orderedCategories.Select((category, index) => (category.LogicalKey, index, category.Name))
            .ToDictionary(x => x.LogicalKey, x => (Position: x.index, CategoryName: x.Name));
        var elementCategoryKeys = graph.Categories.SelectMany(c => c.Elements.Select(e => (e.LogicalKey, CategoryKey: c.LogicalKey)))
            .GroupBy(x => x.LogicalKey).ToDictionary(g => g.Key, g => g.First().CategoryKey);
        var questionCategoryPositions = elements.GroupBy(e => e.LogicalKey)
            .ToDictionary(g => g.Key, g => categoryPositions.GetValueOrDefault(elementCategoryKeys.GetValueOrDefault(g.Key),
                (Position: int.MaxValue, CategoryName: "Unknown category")).Position);
        var categories = graph.Categories.Select(c => c.LogicalKey).ToHashSet();
        var options = elements.SelectMany(e => e.Question!.Options.Select(o => (o.LogicalKey, Question: e.Question, Option: o)))
            .GroupBy(x => x.LogicalKey).ToDictionary(g => g.Key, g => g.First());
        var questionKeysById = elements.Where(e => e.Question is not null)
            .GroupBy(e => e.Question!.Id).ToDictionary(g => g.Key, g => g.First().LogicalKey);
        var ids = definition.Calculations.Select(c => c.Id).ToHashSet();
        bool Name(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 500;
        bool Valid(ValueSource? source) => source is not null && Enum.IsDefined(source.Kind) && (source.Kind switch
        {
            SourceKind.Constant => true,
            SourceKind.Calculation => ids.Contains(source.Key),
            SourceKind.Question => questions.TryGetValue(source.Key, out var e) && e.Question!.AnswerType == AnswerType.Number,
            SourceKind.OptionSelected => options.ContainsKey(source.Key),
            _ => false
        });
        CompositionValueRead ReadDetailed(ValueSource source)
        {
            var value = Read(source);
            switch (source.Kind)
            {
                case SourceKind.Constant:
                    return new(value, []);
                case SourceKind.Calculation:
                {
                    return calculationEffects.TryGetValue(source.Key, out var effects)
                        ? new(value, effects)
                        : new(value, [new(SourceKind.Calculation, source.Key, "unavailable-calculation", value)]);
                }
                case SourceKind.Question when questions.TryGetValue(source.Key, out var element):
                {
                    var questionId = element.Question!.Id;
                    if (!visible.Contains(questionId))
                        return new(value, [new(SourceKind.Question, source.Key, "hidden-question", value)]);
                    return answers.GetValueOrDefault(questionId)?.Number is not null
                        ? new(value, [new(SourceKind.Question, source.Key, "question", value)])
                        : new(value, [new(SourceKind.Question, source.Key, "missing-number", value)]);
                }
                case SourceKind.OptionSelected when options.TryGetValue(source.Key, out var option):
                {
                    if (!visible.Contains(option.Question!.Id))
                        return new(value, [new(SourceKind.OptionSelected, source.Key, "hidden-option", value)]);
                    var selected = value == 1;
                    return new(value,
                        [new(SourceKind.OptionSelected, source.Key, selected ? "selected-option" : "unselected-option", value)]);
                }
                default:
                    return new(value, [new(source.Kind, source.Key, "unavailable-source", value)]);
            }
        }
        decimal? Read(ValueSource source) => source.Kind switch
        {
            SourceKind.Constant => source.Value,
            SourceKind.Calculation => values.GetValueOrDefault(source.Key),
            SourceKind.Question when questions.TryGetValue(source.Key, out var e) => visible.Contains(e.Question!.Id) ? answers.GetValueOrDefault(e.Question.Id)?.Number ?? 0 : 0,
            SourceKind.OptionSelected when options.TryGetValue(source.Key, out var o) => visible.Contains(o.Question!.Id) && answers.GetValueOrDefault(o.Question.Id)?.SelectedOptionIds?.Contains(o.Option.Id) == true ? 1 : 0,
            _ => null
        };
        bool Compare(decimal? left, decimal? right, Comparison comparison)
        {
            if (left is null || right is null) return false;
            return comparison switch
            {
                Comparison.Equal => left == right,
                Comparison.NotEqual => left != right,
                Comparison.GreaterThan => left > right,
                Comparison.GreaterThanOrEqual => left >= right,
                Comparison.LessThan => left < right,
                Comparison.LessThanOrEqual => left <= right,
                _ => false
            };
        }
        CompositionCheckEvaluation EvaluateCheck(ValueCheck check, int index)
        {
            var left = ReadDetailed(check.Left);
            var right = ReadDetailed(check.Right);
            var matches = Compare(left.Value, right.Value, check.Comparison);
            return new(index, left, right, matches);
        }
        bool Check(ValueCheck check)
        {
            var left = Read(check.Left); var right = Read(check.Right);
            return Compare(left, right, check.Comparison);
        }
        bool ValidChecks(List<ValueCheck>? checks) => checks is not null && checks.Count <= 50 && checks.All(c => c is not null && Valid(c.Left) && Valid(c.Right) && Enum.IsDefined(c.Comparison));
        var calculations = definition.Calculations.Where(c => c is not null).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var captureRuleTrace = definition.Rules.Any(rule => rule?.Kind is RulePurpose.Validation or RulePurpose.Score);
        var dependencyMemo = new Dictionary<Guid, HashSet<Guid>>();
        HashSet<Guid> CalculationQuestionDependencies(Guid id, HashSet<Guid> path)
        {
            if (dependencyMemo.TryGetValue(id, out var memoized)) return new HashSet<Guid>(memoized);
            if (!calculations.TryGetValue(id, out var calculation) || !path.Add(id)) return [];
            var result = new HashSet<Guid>();
            foreach (var source in calculation.Sources ?? [])
            {
                if (source is null) continue;
                if (source.Kind == SourceKind.Question && questions.ContainsKey(source.Key)) result.Add(source.Key);
                else if (source.Kind == SourceKind.OptionSelected && options.TryGetValue(source.Key, out var option)
                    && questionKeysById.TryGetValue(option.Question!.Id, out var optionQuestionKey)) result.Add(optionQuestionKey);
                else if (source.Kind == SourceKind.Calculation)
                    result.UnionWith(CalculationQuestionDependencies(source.Key, path));
            }
            path.Remove(id);
            dependencyMemo[id] = new HashSet<Guid>(result);
            return result;
        }
        IEnumerable<Guid> QuestionDependencies(ValueSource? source)
        {
            if (source is null) return [];
            if (source.Kind == SourceKind.Question && questions.ContainsKey(source.Key)) return [source.Key];
            if (source.Kind == SourceKind.OptionSelected && options.TryGetValue(source.Key, out var option)
                && questionKeysById.TryGetValue(option.Question!.Id, out var optionQuestionKey)) return [optionQuestionKey];
            if (source.Kind == SourceKind.Calculation) return CalculationQuestionDependencies(source.Key, []);
            return [];
        }
        if (definition.Groups.Any(g => g.Id == Guid.Empty) || definition.Groups.Select(g => g.Id).Distinct().Count() != definition.Groups.Count
            || definition.Rules.Any(r => r.Id == Guid.Empty) || definition.Rules.Select(r => r.Id).Distinct().Count() != definition.Rules.Count)
            errors.Add("Groups and rules need unique nonempty identifiers.");
        var grouped = new HashSet<Guid>();
        foreach (var group in definition.Groups)
        {
            if (group is null || !Name(group.Name) || !categories.Contains(group.CategoryKey) || group.Columns is null || group.Columns.Count > 8
                || group.Columns.Any(c => !Name(c)) || group.Rows is null || group.Rows.Count is 0 or > 200
                || group.Rows.Any(r => r is null || r.Cells is null || r.Cells.Count != group.Columns.Count || r.Cells.Any(c => c is null || c.Length > 2000)
                    || !questions.ContainsKey(r.QuestionKey) || !graph.Categories.Any(c => c.LogicalKey == group.CategoryKey && c.Elements.Any(e => e.LogicalKey == r.QuestionKey))
                    || !grouped.Add(r.QuestionKey))) errors.Add("Each group needs a name and unique questions from its category, with matching column cells.");
        }
        if (definition.Calculations.Any(c => c is null || c.Id == Guid.Empty || !Name(c.Name) || !categories.Contains(c.CategoryKey) || !Enum.IsDefined(c.Operation)
            || c.Sources is null || c.Sources.Count is 0 or > 200 || c.Operation == CalculationOperation.Percentage && c.Sources.Count != 2 || c.Sources.Any(s => !Valid(s)))
            || ids.Count != definition.Calculations.Count) errors.Add("Calculations need unique identifiers, valid numeric sources, and two sources for a percentage.");
        if (errors.Count == 0)
        {
            var pending = definition.Calculations.ToList();
            while (pending.Count != 0)
            {
                var ready = pending.Where(c => c.Sources.All(s => s.Kind != SourceKind.Calculation || values.ContainsKey(s.Key))).ToArray();
                if (ready.Length == 0) { errors.Add("Calculations cannot contain circular dependencies."); break; }
                foreach (var field in ready)
                {
                    decimal? value;
                    CompositionValueRead[]? sourceReads = null;
                    try
                    {
                        var source = captureRuleTrace
                            ? (sourceReads = field.Sources.Select(ReadDetailed).ToArray()).Select(item => item.Value).ToArray()
                            : field.Sources.Select(Read).ToArray();
                        value = source.Any(v => v is null) ? null : field.Operation == CalculationOperation.Sum
                            ? source.Sum(v => v!.Value) : source[1] == 0 ? null : checked(source[0] / source[1] * 100);
                    }
                    catch (OverflowException) { value = null; errors.Add($"Calculation '{field.Name}' exceeds the supported numeric range."); }
                    values[field.Id] = value;
                    if (sourceReads is not null)
                        calculationEffects[field.Id] = sourceReadsEffects(field.Id, value, sourceReads);
                    if (visibleCategoryIds is null || orderedCategories.Any(c => c.LogicalKey == field.CategoryKey && visibleCategoryIds.Contains(c.Id)))
                        results.Add(new(field.Id, field.Name, field.CategoryKey, value, field.Operation == CalculationOperation.Percentage));
                    pending.Remove(field);
                }
            }
        }

        IReadOnlyList<CompositionInputEffect> sourceReadsEffects(Guid calculationId, decimal? value,
            IReadOnlyList<CompositionValueRead> sourceReads)
        {
            var effects = sourceReads.SelectMany(item => item.Effects)
                .Append(new CompositionInputEffect(SourceKind.Calculation, calculationId,
                    value is null ? "unavailable-calculation" : "calculation", value))
                .DistinctBy(effect => (effect.Kind, effect.Key, effect.Code, effect.UsedValue))
                .ToArray();
            return effects;
        }
        var bands = new Dictionary<(Guid, string), Guid>();
        var combinedScoreRules = new List<CombinedScoreRuleDiagnostic>();
        long maximumPoints = 0;
        foreach (var rule in definition.Rules)
        {
            if (rule is null || !Name(rule.Name) || !categories.Contains(rule.CategoryKey) || !Enum.IsDefined(rule.Kind)
                || !ValidChecks(rule.Checks) || rule.Checks.Count == 0 || !ValidChecks(rule.Conditions) || rule.BandGroup is null || rule.BandGroup.Length > 100
                || rule.Points is < 0 or > 1000000 || rule.Kind == RulePurpose.Validation && !Name(rule.Message)
                || rule.TargetQuestionKey != Guid.Empty && (!questions.ContainsKey(rule.TargetQuestionKey) || !graph.Categories.Any(c => c.LogicalKey == rule.CategoryKey && c.Elements.Any(e => e.LogicalKey == rule.TargetQuestionKey))))
            { errors.Add("Rules need a name, category, valid checks, points, and a validation message where applicable."); continue; }
            if (rule.Kind == RulePurpose.Validation)
            {
                var ruleCategoryPosition = categoryPositions.GetValueOrDefault(rule.CategoryKey,
                    (Position: int.MaxValue, CategoryName: "Unknown category")).Position;
                var latestDependency = rule.Conditions.Concat(rule.Checks)
                    .SelectMany(check => QuestionDependencies(check.Left).Concat(QuestionDependencies(check.Right)))
                    .Where(questionKey => questionCategoryPositions.TryGetValue(questionKey, out var position) && position > ruleCategoryPosition)
                    .Select(questionKey => (Position: questionCategoryPositions[questionKey],
                        Category: categoryPositions.GetValueOrDefault(elementCategoryKeys.GetValueOrDefault(questionKey),
                            (Position: int.MaxValue, CategoryName: "Unknown category")).CategoryName))
                    .OrderByDescending(x => x.Position).FirstOrDefault();
                if (latestDependency != default)
                    errors.Add($"Validation rule '{rule.Name}' depends on an answer in the later category '{latestDependency.Category}'. Attach the rule to that category or a later one.");
            }
            maximumPoints += rule.Kind == RulePurpose.Score ? rule.Points : 0;
            var category = graph.Categories.Single(c => c.LogicalKey == rule.CategoryKey);
            var target = rule.TargetQuestionKey == Guid.Empty ? category.Elements.FirstOrDefault(e => e.Question is not null && visible.Contains(e.Question.Id))
                : questions.GetValueOrDefault(rule.TargetQuestionKey);
            if (errors.Count != 0) continue;
            if (target?.Question is null)
            {
                if (rule.Kind == RulePurpose.Score)
                    combinedScoreRules.Add(new(rule.Id, null, "no-visible-target", 0, null, [], []));
                continue;
            }
            if (!visible.Contains(target.Question.Id))
            {
                if (rule.Kind == RulePurpose.Score)
                    combinedScoreRules.Add(new(rule.Id, target.Question.Id, "target-hidden", 0, null, [], []));
                continue;
            }

            var evaluatedConditions = new List<CompositionCheckEvaluation>();
            var conditionsMatch = true;
            if (rule.Kind == RulePurpose.Validation)
                conditionsMatch = rule.Conditions.All(Check);
            else for (var index = 0; index < rule.Conditions.Count; index++)
            {
                var condition = EvaluateCheck(rule.Conditions[index], index);
                evaluatedConditions.Add(condition);
                if (!condition.Matches) { conditionsMatch = false; break; }
            }
            if (!conditionsMatch)
            {
                if (rule.Kind == RulePurpose.Score)
                    combinedScoreRules.Add(new(rule.Id, target.Question.Id, "condition-not-matched", 0, null, evaluatedConditions, []));
                continue;
            }

            var evaluatedChecks = new List<CompositionCheckEvaluation>();
            var matches = rule.AnyCheck ? false : true;
            for (var index = 0; index < rule.Checks.Count; index++)
            {
                var evaluated = EvaluateCheck(rule.Checks[index], index);
                evaluatedChecks.Add(evaluated);
                if (rule.AnyCheck && evaluated.Matches) { matches = true; break; }
                if (!rule.AnyCheck && !evaluated.Matches) { matches = false; break; }
            }
            if (rule.Kind == RulePurpose.Validation && !matches)
            {
                issues.Add((target.Question.Id, rule.Message));
                if (rule.ShowWhileAnswering) liveIssues.Add(new(rule.Id, target.Question.Id, rule.Message));
                failedValidationRules.Add(new(rule.Id, target.Question.Id, rule.ShowWhileAnswering, rule.AnyCheck,
                    evaluatedChecks));
            }
            if (rule.Kind == RulePurpose.Score)
            {
                if (!matches)
                {
                    combinedScoreRules.Add(new(rule.Id, target.Question.Id, "checks-not-matched", 0, null, evaluatedConditions, evaluatedChecks));
                }
                else if (string.IsNullOrWhiteSpace(rule.BandGroup))
                {
                    awards.Add(new(rule.Id, rule.Name, rule.CategoryKey, rule.Points));
                    combinedScoreRules.Add(new(rule.Id, target.Question.Id, "awarded", rule.Points, null, evaluatedConditions, evaluatedChecks));
                }
                else
                {
                    var band = (rule.CategoryKey, rule.BandGroup.Trim().ToUpperInvariant());
                    if (bands.TryGetValue(band, out var winner))
                        combinedScoreRules.Add(new(rule.Id, target.Question.Id, "band-suppressed", 0, winner, evaluatedConditions, evaluatedChecks));
                    else
                    {
                        bands[band] = rule.Id;
                        awards.Add(new(rule.Id, rule.Name, rule.CategoryKey, rule.Points));
                        combinedScoreRules.Add(new(rule.Id, target.Question.Id, "awarded", rule.Points, null, evaluatedConditions, evaluatedChecks));
                    }
                }
            }
        }
        var questionMaximumPoints = elements.SelectMany(e => e.Question!.Rules.Select(rule =>
            ScoringAward.TryGetMaximumAward(e.Question!, rule, out var ruleMaximum) ? (long)ruleMaximum : 0L)).Sum();
        if (maximumPoints + questionMaximumPoints > int.MaxValue) errors.Add("Combined scoring exceeds the supported total score range.");
        if (maximumPoints > 100000000) errors.Add("Combined calculated scoring exceeds the supported limit.");
        return new CompositionResult(results, errors.Count == 0 ? awards : [], errors, issues)
        {
            LiveAnswerErrors = liveIssues,
            FailedValidationRules = errors.Count == 0 ? failedValidationRules : null,
            CombinedScoreRules = errors.Count == 0 ? combinedScoreRules : null
        };
    }
}
