using System.Globalization;
using System.Text.Json;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Composition;

public static class DynamicTextEngine
{
    private const int MaximumVariables = 200;
    private const int MaximumTemplates = 500;
    private const int MaximumParts = 100;
    private const int MaximumSources = 100;
    private const int MaximumTextLength = 4000;
    private const int MaximumDependencyDepth = 100;
    private static readonly HashSet<string> DateFormats = ["yyyy-MM-dd", "MM-dd-yyyy", "dd-MM-yyyy", "MMM d, yyyy"];

    public static DynamicTextResult Evaluate(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers,
        IReadOnlySet<int> visibleQuestionIds, IReadOnlySet<int> visibleCategoryIds, CompositionResult composition,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(visibleQuestionIds);
        ArgumentNullException.ThrowIfNull(visibleCategoryIds);
        ArgumentNullException.ThrowIfNull(composition);

        CompositionDefinition definition;
        try { definition = CompositionDefinition.Read(graph.Version.CompositionJson); }
        catch (JsonException) { return Empty(evaluatedAtUtc, "UTC", ["Dynamic text configuration is malformed."]); }

        var config = definition.DynamicText;
        if (config is null || config.Variables is null || config.Templates is null
            || config.Variables.Count > MaximumVariables || config.Templates.Count > MaximumTemplates)
            return Empty(evaluatedAtUtc, "UTC", ["Dynamic text configuration exceeds supported limits or contains missing lists."]);

        var errors = new List<string>();
        TimeZoneInfo timeZone;
        try
        {
            if (string.IsNullOrWhiteSpace(config.TimeZoneId) || config.TimeZoneId.Length > 100)
                throw new TimeZoneNotFoundException();
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
        }
        catch (TimeZoneNotFoundException) { errors.Add("Dynamic text uses an unknown time zone."); timeZone = TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { errors.Add("Dynamic text uses an invalid time zone."); timeZone = TimeZoneInfo.Utc; }

        var elements = graph.Categories.SelectMany(c => c.Elements).ToArray();
        var elementByKey = elements.GroupBy(e => e.LogicalKey).ToDictionary(g => g.Key, g => g.First());
        var categoryByKey = graph.Categories.GroupBy(c => c.LogicalKey).ToDictionary(g => g.Key, g => g.First());
        var elementCategory = graph.Categories.SelectMany(c => c.Elements.Select(e => (e.LogicalKey, Category: c)))
            .GroupBy(x => x.LogicalKey).ToDictionary(g => g.Key, g => g.First().Category);
        var variables = config.Variables.Where(v => v is not null).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        var duplicateVariableIds = config.Variables.Where(v => v is not null).GroupBy(v => v.Id).Any(g => g.Key == Guid.Empty || g.Count() > 1);
        if (duplicateVariableIds) errors.Add("Dynamic variables need unique nonempty identifiers.");

        if (definition.Calculations is null)
            errors.Add("Composition configuration is missing its calculations list.");
        var calculations = (definition.Calculations ?? []).Where(c => c is not null)
            .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var calculationValues = composition.Values.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        var calculationCategories = calculations.ToDictionary(pair => pair.Key, pair => pair.Value.CategoryKey);

        string? SourceShapeError(DynamicSource? source)
        {
            if (source is null || !Enum.IsDefined(source.Kind)) return "A dynamic source is missing or unsupported.";
            var emptyKey = source.Key == Guid.Empty;
            return source.Kind switch
            {
                DynamicSourceKind.Text when source.Text is null || source.Text.Length > MaximumTextLength || !emptyKey || source.Number is not null || source.Date is not null
                    => "A text source has invalid data.",
                DynamicSourceKind.Number when source.Number is null || !emptyKey || source.Text is not null || source.Date is not null
                    => "A number source has invalid data.",
                DynamicSourceKind.Date when source.Date is null || !emptyKey || source.Text is not null || source.Number is not null
                    => "A date source has invalid data.",
                DynamicSourceKind.Question or DynamicSourceKind.Variable or DynamicSourceKind.Calculation when emptyKey || source.Text is not null || source.Number is not null || source.Date is not null
                    => "A referenced source has invalid data.",
                DynamicSourceKind.Today when !emptyKey || source.Text is not null || source.Number is not null || source.Date is not null
                    => "A today source has invalid data.",
                _ => null
            };
        }

        var inferred = new Dictionary<Guid, DynamicValueKind>();
        var completedInference = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();
        DynamicValueKind? InferVariable(Guid id, int depth = 0)
        {
            if (depth > MaximumDependencyDepth) { errors.Add("Dynamic variable dependencies exceed the supported depth."); return null; }
            if (inferred.TryGetValue(id, out var cached)) return cached;
            if (completedInference.Contains(id)) return null;
            if (!variables.TryGetValue(id, out var variable)) { errors.Add("A dynamic variable source refers to a missing variable."); return null; }
            if (!visiting.Add(id)) { errors.Add("Dynamic variables cannot contain circular dependencies."); return null; }
            var sources = variable.Sources;
            if (sources is null || sources.Count is 0 or > MaximumSources)
            {
                errors.Add($"Dynamic variable '{variable.Name}' requires a supported number of sources.");
                visiting.Remove(id);
                return null;
            }
            foreach (var source in sources)
                if (SourceShapeError(source) is { } shapeError) errors.Add(shapeError);
            var sourceKinds = sources.Select(source => InferSourceKind(source, depth + 1)).ToArray();
            DynamicValueKind? output = null;
            switch (variable.Operation)
            {
                case DynamicOperation.Value when sourceKinds.Length == 1:
                    output = sourceKinds[0];
                    break;
                case DynamicOperation.JoinText when sourceKinds.Length >= 1 && sourceKinds.All(k => k is DynamicValueKind.Text or DynamicValueKind.TextList):
                    if (variable.Separator is null || variable.Separator.Length > MaximumTextLength) errors.Add("A dynamic text separator is too long.");
                    output = DynamicValueKind.Text;
                    break;
                case DynamicOperation.FormatDate when sourceKinds.Length == 1 && sourceKinds[0] == DynamicValueKind.Date:
                    if (!DateFormats.Contains(variable.DateFormat ?? "")) errors.Add("Dynamic date formatting is not supported.");
                    output = DynamicValueKind.Text;
                    break;
                case DynamicOperation.Age when sourceKinds.Length == 2 && sourceKinds.All(k => k == DynamicValueKind.Date):
                    output = DynamicValueKind.Number;
                    break;
                case DynamicOperation.Add or DynamicOperation.Multiply when sourceKinds.Length >= 2 && sourceKinds.All(k => k == DynamicValueKind.Number):
                    output = DynamicValueKind.Number;
                    break;
                case DynamicOperation.Subtract or DynamicOperation.Divide when sourceKinds.Length == 2 && sourceKinds.All(k => k == DynamicValueKind.Number):
                    output = DynamicValueKind.Number;
                    break;
                default:
                    errors.Add($"Dynamic variable '{variable.Name}' has an invalid operation or source type.");
                    break;
            }
            if (!Enum.IsDefined(variable.Operation) || variable.Id == Guid.Empty || string.IsNullOrWhiteSpace(variable.Name)
                || variable.Name.Length > 200 || variable.CategoryKey is Guid owner && !categoryByKey.ContainsKey(owner))
                errors.Add("Dynamic variables need a supported operation, name, and category.");
            visiting.Remove(id);
            completedInference.Add(id);
            if (output is not null) inferred[id] = output.Value;
            return output;
        }

        DynamicValueKind? InferSourceKind(DynamicSource source, int depth)
        {
            if (SourceShapeError(source) is not null) return null;
            switch (source.Kind)
            {
                case DynamicSourceKind.Text: return DynamicValueKind.Text;
                case DynamicSourceKind.Number: return DynamicValueKind.Number;
                case DynamicSourceKind.Date: return DynamicValueKind.Date;
                case DynamicSourceKind.Today: return DynamicValueKind.Date;
                case DynamicSourceKind.Variable: return InferVariable(source.Key, depth);
                case DynamicSourceKind.Calculation:
                    if (!calculations.ContainsKey(source.Key)) { errors.Add("A dynamic source refers to a missing calculation."); return null; }
                    return DynamicValueKind.Number;
                case DynamicSourceKind.Question:
                    if (!elementByKey.TryGetValue(source.Key, out var element) || element.Question is not { } question)
                    { errors.Add("A dynamic source refers to a missing question."); return null; }
                    return question.AnswerType switch
                    {
                        AnswerType.Textbox or AnswerType.Textarea or AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.UploadDocument => DynamicValueKind.Text,
                        AnswerType.Checkboxes => DynamicValueKind.TextList,
                        AnswerType.Number => DynamicValueKind.Number,
                        AnswerType.Date => DynamicValueKind.Date,
                        _ => AddUnsupportedQuestionSource()
                    };
                default: errors.Add("A dynamic source kind is not supported."); return null;
            }
        }
        DynamicValueKind? AddUnsupportedQuestionSource() { errors.Add("A dynamic source uses an unsupported question type."); return null; }

        foreach (var variable in config.Variables)
            if (variable is null) errors.Add("Dynamic variables cannot be missing.");
            else _ = InferVariable(variable.Id);

        var templateKeys = new HashSet<(DynamicTextSlot Slot, Guid Owner)>();
        if (config.Templates.Any(t => t is null)) errors.Add("Dynamic templates cannot be missing.");
        foreach (var template in config.Templates.Where(t => t is not null))
        {
            if (!Enum.IsDefined(template.Slot) || template.Parts is null || template.Parts.Count > MaximumParts
                || !templateKeys.Add((template.Slot, template.OwnerKey)))
                errors.Add("Dynamic templates need unique supported slots and bounded parts.");
            var validOwner = template.Slot switch
            {
                DynamicTextSlot.ElementLabel or DynamicTextSlot.ElementHelpText => template.OwnerKey != Guid.Empty && elementByKey.ContainsKey(template.OwnerKey),
                DynamicTextSlot.ReviewSummary or DynamicTextSlot.ConfirmationText => template.OwnerKey == Guid.Empty,
                _ => false
            };
            if (!validOwner) errors.Add("A dynamic template has an invalid owner.");
            if (template.HideWhenUnavailable && !(template.Slot == DynamicTextSlot.ReviewSummary
                || template.Slot == DynamicTextSlot.ElementLabel && elementByKey.TryGetValue(template.OwnerKey, out var labelOwner) && labelOwner.Kind == ElementKind.Content))
                errors.Add("Unavailable dynamic text can only hide content labels or a review summary.");
            if (template.Parts is null) continue;
            if (template.Parts.Where(part => part is not null && part.Kind == DynamicPartKind.Literal)
                .Sum(part => (long)(part.Text?.Length ?? 0)) > MaximumTextLength)
                errors.Add("A dynamic template exceeds the supported output length.");
            foreach (var part in template.Parts)
            {
                if (part is null || !Enum.IsDefined(part.Kind)) { errors.Add("A dynamic template part is missing or unsupported."); continue; }
                if (part.Kind == DynamicPartKind.Literal)
                {
                    if (part.Text is null || part.Text.Length > MaximumTextLength || part.Source is not null)
                        errors.Add("A dynamic literal is invalid or too long.");
                }
                else if (SourceShapeError(part.Source) is { } error) errors.Add(error);
                else if (part.Source is not null) _ = InferSourceKind(part.Source, 0);
                if (part.Fallback is null || part.Fallback.Length > MaximumTextLength) errors.Add("A dynamic fallback is invalid or too long.");
            }
        }
        if (errors.Count != 0) return Empty(evaluatedAtUtc, config.TimeZoneId ?? "UTC", errors.Distinct().ToArray());

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(evaluatedAtUtc, timeZone).DateTime);
        var values = new Dictionary<Guid, DynamicValue>();
        var ordered = new List<DynamicVariable>();
        var remaining = config.Variables.ToList();
        var orderedIds = new HashSet<Guid>();
        while (remaining.Count != 0)
        {
            var ready = remaining.Where(v => v.Sources.All(s => s.Kind != DynamicSourceKind.Variable || orderedIds.Contains(s.Key))).ToArray();
            if (ready.Length == 0) return Empty(evaluatedAtUtc, config.TimeZoneId, ["Dynamic variables cannot contain circular dependencies."]);
            foreach (var variable in ready) { ordered.Add(variable); orderedIds.Add(variable.Id); remaining.Remove(variable); }
        }

        DynamicValue Missing(DynamicValueKind kind) => new(kind, DynamicValueState.Missing);
        DynamicValue NotApplicable(DynamicValueKind kind) => new(kind, DynamicValueState.NotApplicable);
        DynamicValue Invalid(DynamicValueKind kind) => new(kind, DynamicValueState.Invalid);
        DynamicValue Read(DynamicSource source)
        {
            switch (source.Kind)
            {
                case DynamicSourceKind.Text: return new(DynamicValueKind.Text, DynamicValueState.Present, Text: source.Text);
                case DynamicSourceKind.Number: return new(DynamicValueKind.Number, DynamicValueState.Present, Number: source.Number);
                case DynamicSourceKind.Date: return new(DynamicValueKind.Date, DynamicValueState.Present, Date: source.Date);
                case DynamicSourceKind.Today: return new(DynamicValueKind.Date, DynamicValueState.Present, Date: localDate);
                case DynamicSourceKind.Variable: return values.GetValueOrDefault(source.Key, Invalid(inferred[source.Key]));
                case DynamicSourceKind.Calculation:
                    if (!calculations.TryGetValue(source.Key, out var field)) return Invalid(DynamicValueKind.Number);
                    if (!categoryByKey.TryGetValue(field.CategoryKey, out var category)) return Invalid(DynamicValueKind.Number);
                    if (!visibleCategoryIds.Contains(category.Id)) return NotApplicable(DynamicValueKind.Number);
                    if (!calculationValues.TryGetValue(source.Key, out var calculated)) return Missing(DynamicValueKind.Number);
                    return calculated.Value is decimal value ? new(DynamicValueKind.Number, DynamicValueState.Present, Number: value) : Missing(DynamicValueKind.Number);
                case DynamicSourceKind.Question:
                    if (!elementByKey.TryGetValue(source.Key, out var element) || element.Question is not { } question) return Invalid(DynamicValueKind.Text);
                    var kind = inferredQuestionKind(question.AnswerType);
                    if (!elementCategory.TryGetValue(source.Key, out var categoryForQuestion)) return Invalid(kind);
                    if (!visibleCategoryIds.Contains(categoryForQuestion.Id) || !visibleQuestionIds.Contains(question.Id)) return NotApplicable(kind);
                    return ReadQuestion(question, answers.GetValueOrDefault(question.Id), kind);
                default: return Invalid(DynamicValueKind.Text);
            }
        }

        foreach (var variable in ordered)
        {
            var kind = inferred[variable.Id];
            if (variable.CategoryKey is Guid categoryKey && categoryByKey.TryGetValue(categoryKey, out var category) && !visibleCategoryIds.Contains(category.Id))
            { values[variable.Id] = NotApplicable(kind); continue; }
            var sources = variable.Sources.Select(Read).ToArray();
            values[variable.Id] = Apply(variable, sources, kind, localDate);
        }

        var rendered = new List<ResolvedDynamicText>();
        foreach (var template in config.Templates)
        {
            if (template.Slot is DynamicTextSlot.ElementLabel or DynamicTextSlot.ElementHelpText)
            {
                var owner = elementByKey[template.OwnerKey];
                var ownerCategory = elementCategory[template.OwnerKey];
                if (!visibleCategoryIds.Contains(ownerCategory.Id)
                    || owner.Question is { } ownerQuestion && !visibleQuestionIds.Contains(ownerQuestion.Id)) continue;
            }
            var parts = new List<string>();
            var unavailable = false;
            foreach (var part in template.Parts)
            {
                if (part.Kind == DynamicPartKind.Literal) { parts.Add(part.Text); continue; }
                var value = Read(part.Source!);
                if (value.State != DynamicValueState.Present)
                {
                    unavailable = true;
                    parts.Add(part.Fallback);
                }
                else parts.Add(FormatValue(value));
            }
            var text = string.Concat(parts);
            if (text.Length > MaximumTextLength) continue;
            if (template.Slot == DynamicTextSlot.ElementLabel && elementByKey.TryGetValue(template.OwnerKey, out var labelOwner)
                && labelOwner.Question is not null && string.IsNullOrWhiteSpace(text)) continue;
            rendered.Add(new(template.Slot, template.OwnerKey, text) { IsHidden = template.HideWhenUnavailable && unavailable });
        }
        return new DynamicTextResult(evaluatedAtUtc, config.TimeZoneId, localDate,
            ordered.Select(v => new DynamicVariableResult(v.Id, v.Name, values[v.Id])).ToArray(), rendered, []);

        DynamicValueKind inferredQuestionKind(AnswerType answerType) => answerType switch
        {
            AnswerType.Checkboxes => DynamicValueKind.TextList,
            AnswerType.Number => DynamicValueKind.Number,
            AnswerType.Date => DynamicValueKind.Date,
            _ => DynamicValueKind.Text
        };
    }

    private static DynamicValue ReadQuestion(Question question, AnswerValue? answer, DynamicValueKind kind)
    {
        if (answer is null) return new(kind, DynamicValueState.Missing);
        var valid = question.AnswerType switch
        {
            AnswerType.Textbox or AnswerType.Textarea => answer.Number is null && answer.SelectedOptionIds is null
                && (answer.Text is null || answer.Text.Length <= 4000 && (question.MaxLength is null || answer.Text.Length <= question.MaxLength)),
            AnswerType.UploadDocument => answer.Number is null && answer.SelectedOptionIds is null && (answer.Text is null || answer.Text.Length <= 255),
            AnswerType.Number => answer.Text is null && answer.SelectedOptionIds is null && (answer.Number is null
                || (!question.WholeNumbersOnly || decimal.Truncate(answer.Number.Value) == answer.Number)
                    && (question.MinValue is null || answer.Number >= question.MinValue) && (question.MaxValue is null || answer.Number <= question.MaxValue)),
            AnswerType.Date => answer.Number is null && answer.SelectedOptionIds is null
                && (string.IsNullOrWhiteSpace(answer.Text) || DateOnly.TryParseExact(answer.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)),
            AnswerType.Dropdown or AnswerType.RadioGroup => answer.Text is null && answer.Number is null && ValidOptions(question, answer.SelectedOptionIds, 1),
            AnswerType.Checkboxes => answer.Text is null && answer.Number is null && ValidOptions(question, answer.SelectedOptionIds, question.MaxSelections),
            _ => false
        };
        if (!valid) return new(kind, DynamicValueState.Invalid);
        if (answer.SelectedOptionIds is { Count: > 0 } selected && question.MinSelections is int minimum && selected.Count < minimum)
            return new(kind, DynamicValueState.Invalid);
        if (!AnswerSemantics.IsAnswered(question.AnswerType, answer)) return new(kind, DynamicValueState.Missing);
        return question.AnswerType switch
        {
            AnswerType.Number => new(kind, DynamicValueState.Present, Number: answer.Number),
            AnswerType.Date when DateOnly.TryParseExact(answer.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                => new(kind, DynamicValueState.Present, Date: date),
            AnswerType.Checkboxes => new(kind, DynamicValueState.Present, Items: question.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
                .Where(o => answer.SelectedOptionIds!.Contains(o.Id)).Select(o => o.Label).ToArray()),
            AnswerType.Dropdown or AnswerType.RadioGroup => new(kind, DynamicValueState.Present,
                Text: question.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).First(o => o.Id == answer.SelectedOptionIds!.Single()).Label),
            _ => new(kind, DynamicValueState.Present, Text: answer.Text)
        };
    }

    private static bool ValidOptions(Question question, IReadOnlyCollection<int>? selected, int? max)
    {
        if (selected is null) return true;
        var allowed = question.Options.Select(x => x.Id).ToHashSet();
        return selected.Distinct().Count() == selected.Count && (max is null || selected.Count <= max) && selected.All(allowed.Contains);
    }

    private static DynamicValue Apply(DynamicVariable variable, DynamicValue[] sources, DynamicValueKind outputKind, DateOnly referenceDate)
    {
        var invalid = new DynamicValue(outputKind, DynamicValueState.Invalid);
        var firstUnavailable = sources.FirstOrDefault(x => x.State is DynamicValueState.Invalid);
        if (firstUnavailable is not null) return invalid;
        if (variable.Operation == DynamicOperation.JoinText)
        {
            if (!variable.SkipEmpty && sources.Any(x => x.State != DynamicValueState.Present))
                return new(outputKind, sources.Any(x => x.State == DynamicValueState.NotApplicable) ? DynamicValueState.NotApplicable : DynamicValueState.Missing);
            var pieces = new List<string>();
            foreach (var source in sources.Where(x => x.State == DynamicValueState.Present))
            {
                IEnumerable<string> values = source.Kind == DynamicValueKind.TextList ? source.Items ?? [] : new[] { source.Text ?? "" };
                foreach (var piece in values.Select(value => value.Trim()))
                    if (!variable.SkipEmpty || piece.Length > 0) pieces.Add(piece);
            }
            if (pieces.Count == 0)
                return new(outputKind, sources.Any(x => x.State == DynamicValueState.NotApplicable) ? DynamicValueState.NotApplicable : DynamicValueState.Missing);
            var joined = string.Join(variable.Separator, pieces);
            return joined.Length <= MaximumTextLength ? new(outputKind, DynamicValueState.Present, Text: joined) : invalid;
        }
        if (sources.Any(x => x.State != DynamicValueState.Present))
            return new(outputKind, sources.Any(x => x.State == DynamicValueState.NotApplicable) ? DynamicValueState.NotApplicable : DynamicValueState.Missing);

        try
        {
            switch (variable.Operation)
            {
                case DynamicOperation.Value: return sources[0];
                case DynamicOperation.FormatDate:
                    return new(outputKind, DynamicValueState.Present, Text: sources[0].Date!.Value.ToString(variable.DateFormat, CultureInfo.InvariantCulture));
                case DynamicOperation.Age:
                    var birthDate = sources[0].Date!.Value;
                    var asOf = sources[1].Date!.Value;
                    if (birthDate > asOf) return invalid;
                    var years = asOf.Year - birthDate.Year;
                    if (birthDate.AddYears(years) > asOf) years--;
                    return new(outputKind, DynamicValueState.Present, Number: years);
                case DynamicOperation.Add:
                    decimal sum = 0;
                    foreach (var source in sources) sum = checked(sum + source.Number!.Value);
                    return new(outputKind, DynamicValueState.Present, Number: sum);
                case DynamicOperation.Multiply:
                    decimal product = 1;
                    foreach (var source in sources) product = checked(product * source.Number!.Value);
                    return new(outputKind, DynamicValueState.Present, Number: product);
                case DynamicOperation.Subtract:
                    return new(outputKind, DynamicValueState.Present, Number: checked(sources[0].Number!.Value - sources[1].Number!.Value));
                case DynamicOperation.Divide:
                    if (sources[1].Number == 0) return invalid;
                    return new(outputKind, DynamicValueState.Present, Number: checked(sources[0].Number!.Value / sources[1].Number!.Value));
                default: return invalid;
            }
        }
        catch (OverflowException) { return invalid; }
    }

    private static string FormatValue(DynamicValue value) => value.Kind switch
    {
        DynamicValueKind.Text => value.Text ?? "",
        DynamicValueKind.TextList => string.Join(" ", value.Items ?? []),
        DynamicValueKind.Number => value.Number?.ToString(CultureInfo.InvariantCulture) ?? "",
        DynamicValueKind.Date => value.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        _ => ""
    };

    private static DynamicTextResult Empty(DateTimeOffset instant, string timeZone, IReadOnlyList<string> errors) =>
        new(instant, timeZone, DateOnly.FromDateTime(instant.UtcDateTime), [], [], errors);
}
