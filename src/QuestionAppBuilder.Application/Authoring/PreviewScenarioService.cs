using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Interfaces;

namespace QuestionAppBuilder.Application.Authoring;

/// <summary>Persists reusable synthetic preview inputs and evaluates them against saved drafts only.</summary>
public sealed class PreviewScenarioService : IPreviewScenarioService
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IDraftService _drafts;
    private readonly IPreviewScenarioRepository _scenarios;
    private readonly IDefinitionEvaluator _evaluator;

    public PreviewScenarioService(IDraftService drafts, IPreviewScenarioRepository scenarios, IDefinitionEvaluator evaluator)
    {
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public async Task<IReadOnlyList<PreviewScenarioSummary>> ListAsync(int applicationId, CancellationToken ct = default)
    {
        var rows = await _scenarios.ListAsync(applicationId, ct);
        return rows.Select(ToSummary).ToArray();
    }

    public async Task<PreviewScenarioSummary> SaveAsync(SavePreviewScenarioRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Draft is null || request.Draft.ExpectedRevision is null)
            throw new DefinitionValidationException("The current draft revision is required to save a scenario.");
        if (request.ScenarioId is null && request.ExpectedScenarioRevision is not null
            || request.ScenarioId is Guid scenarioId && scenarioId == Guid.Empty
            || request.ScenarioId is not null && request.ExpectedScenarioRevision is null)
            throw new DefinitionValidationException("The scenario identifier and scenario revision do not match a create or update request.");
        ValidateExpectations(request.ExpectedValid, request.ExpectedTotal);
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > PreviewScenarioLimits.MaximumNameLength)
            throw new DefinitionValidationException($"Scenario names must contain 1 to {PreviewScenarioLimits.MaximumNameLength} characters.");
        if (request.Answers is null || request.Answers.Count > PreviewScenarioLimits.MaximumAnswers)
            throw new DefinitionValidationException($"A scenario can contain at most {PreviewScenarioLimits.MaximumAnswers} answers.");

        var snapshot = await _drafts.GetAsync(request.Draft.ApplicationId, ct);
        EnsureDraft(snapshot, request.Draft);
        var elements = ElementsByQuestionId(snapshot.Definition);
        if (elements.Count != snapshot.Definition.Categories.SelectMany(category => category.Elements)
                .Count(element => element.Question is not null))
            throw new DefinitionValidationException("Question references are ambiguous in this draft; repair the definition before saving a scenario.");
        var questionLogicalKeys = elements.Values.Where(element => element.LogicalKey == Guid.Empty)
            .Select(element => element.Question!.Id).ToHashSet();
        var duplicateElementKeys = elements.Values.Where(element => element.LogicalKey != Guid.Empty)
            .GroupBy(element => element.LogicalKey).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var payloadAnswers = new List<PreviewScenarioAnswer>(request.Answers.Count);
        foreach (var (questionId, input) in request.Answers)
        {
            if (!elements.TryGetValue(questionId, out var element) || element.Question is null)
                throw new DefinitionValidationException("A scenario answer references a question outside the current draft.");
            if (input is null)
                throw new DefinitionValidationException("A scenario answer is incomplete.");
            var question = element.Question ?? throw new InvalidOperationException("A resolved scenario element has no question.");
            if (questionLogicalKeys.Contains(question.Id) || duplicateElementKeys.Contains(element.LogicalKey))
                throw new DefinitionValidationException("Question logical keys are missing or ambiguous in this draft.");
            if (question.Options is null || question.Options.Any(option => option is null))
                throw new DefinitionValidationException("Answer options are incomplete in this draft; repair the definition before saving a scenario.");
            var selectedKeys = CaptureSelectedOptions(question, input.SelectedOptionIds);
            if (question.AnswerType is not (AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes) && selectedKeys.Count > 0)
                throw new DefinitionValidationException("Only choice questions can store selected options in a preview scenario.");
            if (question.AnswerType == AnswerType.UploadDocument
                && (!string.IsNullOrEmpty(input.Text) || !string.IsNullOrEmpty(input.Number) || selectedKeys.Count > 0))
                throw new DefinitionValidationException("Uploaded files are not supported in preview scenarios; clear the file input and save an empty answer.");
            if (input.Number is not null && input.Number.Length > 128)
                throw new DefinitionValidationException("A scenario number must be 128 characters or fewer.");
            if (input.Number is not null && input.Number.Length > 0
                && !decimal.TryParse(input.Number, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                throw new DefinitionValidationException("Scenario numbers must use a valid invariant decimal value.");
            payloadAnswers.Add(new PreviewScenarioAnswer(element.LogicalKey, question.AnswerType,
                input.Text, input.Number, selectedKeys));
        }

        var payload = new PreviewScenarioPayload(CurrentSchemaVersion, payloadAnswers, request.ExpectedValid, request.ExpectedTotal);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > PreviewScenarioLimits.MaximumPayloadBytes)
            throw new DefinitionValidationException($"Scenario data must be {PreviewScenarioLimits.MaximumPayloadBytes / 1024} KB or smaller.");
        var now = DateTime.UtcNow;
        var entity = new PreviewScenario
        {
            Id = request.ScenarioId ?? Guid.NewGuid(), ApplicationId = request.Draft.ApplicationId,
            Name = name, PayloadJson = json, CreatedUtc = now, UpdatedUtc = now
        };
        var saved = await _scenarios.SaveAsync(entity, request.Draft.ExpectedVersionId,
            request.Draft.ExpectedRevision, request.ExpectedScenarioRevision, ct);
        return ToSummary(saved);
    }

    public async Task<LoadedPreviewScenario> LoadAsync(PreviewScenarioDraft draft, Guid scenarioId, CancellationToken ct = default)
    {
        if (draft is null || draft.ExpectedRevision is null || scenarioId == Guid.Empty)
            throw new DefinitionValidationException("A current draft revision and scenario identifier are required.");
        var snapshot = await _drafts.GetAsync(draft.ApplicationId, ct);
        EnsureDraft(snapshot, draft);
        var row = await _scenarios.GetAsync(draft.ApplicationId, scenarioId, ct)
            ?? throw new NotFoundException("The preview scenario was not found.");
        var payload = ReadPayload(row, out var payloadIssue);
        if (payload is null)
            return new(ToSummary(row), snapshot.Definition.Version.Id, snapshot.Revision, new Dictionary<int, PreviewScenarioInput>(), [payloadIssue!]);
        var resolved = ResolveAnswers(snapshot.Definition, payload, out var issues);
        return new(ToSummary(row), snapshot.Definition.Version.Id, snapshot.Revision,
            issues.Count == 0 ? resolved : new Dictionary<int, PreviewScenarioInput>(), issues);
    }

    public Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision, CancellationToken ct = default)
    {
        if (scenarioId == Guid.Empty || expectedScenarioRevision is null)
            throw new DefinitionValidationException("A scenario identifier and scenario revision are required to delete a scenario.");
        return _scenarios.DeleteAsync(applicationId, scenarioId, expectedScenarioRevision, ct);
    }

    public async Task<PreviewScenarioRunReport> RunAsync(PreviewScenarioDraft draft, Guid? scenarioId = null,
        CancellationToken ct = default)
    {
        if (draft is null || draft.ExpectedRevision is null || scenarioId == Guid.Empty)
            throw new DefinitionValidationException("A current draft revision is required; a selected scenario identifier cannot be empty.");
        var snapshot = await _drafts.GetAsync(draft.ApplicationId, ct);
        EnsureDraft(snapshot, draft);
        var rows = scenarioId is Guid id
            ? new[] { await _scenarios.GetAsync(draft.ApplicationId, id, ct) ?? throw new NotFoundException("The preview scenario was not found.") }
            : (await _scenarios.ListAsync(draft.ApplicationId, ct)).ToArray();
        var results = new List<PreviewScenarioRunResult>(rows.Length);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var payload = ReadPayload(row, out var payloadIssue);
            if (payload is null)
            {
                results.Add(NeedsRepair(row, null, null, [payloadIssue!]));
                continue;
            }
            var answers = ResolveAnswers(snapshot.Definition, payload, out var issues);
            if (issues.Count != 0)
            {
                results.Add(NeedsRepair(row, payload.ExpectedValid, payload.ExpectedTotal, issues));
                continue;
            }
            var evaluationAnswers = new Dictionary<int, AnswerValue>();
            foreach (var (questionId, input) in answers)
            {
                decimal? number = null;
                if (!string.IsNullOrWhiteSpace(input.Number))
                {
                    if (!decimal.TryParse(input.Number, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    {
                        issues.Add("A saved numeric input is no longer a valid invariant decimal value.");
                        break;
                    }
                    number = parsed;
                }
                evaluationAnswers[questionId] = new AnswerValue(input.Text, number, input.SelectedOptionIds);
            }
            if (issues.Count != 0)
            {
                results.Add(NeedsRepair(row, payload.ExpectedValid, payload.ExpectedTotal, issues));
                continue;
            }
            var result = _evaluator.Evaluate(snapshot.Definition, evaluationAnswers, requireComplete: true);
            if (result.DefinitionIssues.Count != 0)
            {
                var definitionMessages = result.DefinitionIssues.Select(issue => issue.Message).Distinct().ToArray();
                results.Add(NeedsRepair(row, payload.ExpectedValid, payload.ExpectedTotal, definitionMessages));
                continue;
            }
            var actualValid = result.AnswerIssues.Count == 0;
            var actualTotal = result.Score.Total;
            var messages = new List<string>();
            if (payload.ExpectedValid is bool expectedValid && actualValid != expectedValid)
                messages.Add($"Expected validity {Word(expectedValid)}, but actual validity was {Word(actualValid)}.");
            if (payload.ExpectedTotal is int expectedTotal && actualTotal != expectedTotal)
                messages.Add($"Expected total {expectedTotal}, but actual total was {actualTotal}.");
            results.Add(new(row.Id, row.Name, row.RowVersion.ToArray(), messages.Count == 0 ? PreviewScenarioRunStatus.Passed : PreviewScenarioRunStatus.Failed,
                payload.ExpectedValid, payload.ExpectedTotal, actualValid, actualTotal, messages));
        }
        return new(snapshot.Definition.Version.Id, snapshot.Revision.ToArray(), results);
    }

    private static Dictionary<int, PreviewScenarioInput> ResolveAnswers(ApplicationVersionGraph graph,
        PreviewScenarioPayload payload, out List<string> issues)
    {
        issues = [];
        if (payload.SchemaVersion != CurrentSchemaVersion)
        {
            issues.Add($"Scenario schema version {payload.SchemaVersion} is unsupported; save the scenario again.");
            return [];
        }
        if (payload.Answers is null || payload.Answers.Count > PreviewScenarioLimits.MaximumAnswers)
        {
            issues.Add("Scenario answer data is missing or exceeds the supported limit.");
            return [];
        }
        var questionElements = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).ToArray();
        var elements = questionElements.Where(element => element.LogicalKey != Guid.Empty)
            .GroupBy(element => element.LogicalKey).ToDictionary(group => group.Key, group => group.ToArray());
        var duplicateQuestionKeys = payload.Answers.Where(answer => answer is not null)
            .GroupBy(answer => answer.ElementKey).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        if (questionElements.Any(element => element.LogicalKey == Guid.Empty)
            || questionElements.GroupBy(element => element.LogicalKey).Any(group => group.Key != Guid.Empty && group.Count() > 1))
        {
            issues.Add("Question logical keys are missing or ambiguous in this draft; repair the definition before running scenarios.");
            return [];
        }
        var answers = new Dictionary<int, PreviewScenarioInput>();
        foreach (var saved in payload.Answers)
        {
            if (saved is null || saved.ElementKey == Guid.Empty || duplicateQuestionKeys.Contains(saved.ElementKey)
                || !elements.TryGetValue(saved.ElementKey, out var elementMatches) || elementMatches.Length != 1
                || elementMatches[0].Question is null)
            {
                issues.Add(duplicateQuestionKeys.Contains(saved?.ElementKey ?? Guid.Empty)
                    ? "Scenario data contains duplicate question references; repair or replace this scenario."
                    : "A saved question no longer exists in this draft.");
                continue;
            }
            var element = elementMatches[0];
            var question = element.Question ?? throw new InvalidOperationException("A resolved scenario element has no question.");
            if (question.Options is null || question.Options.Any(option => option is null))
            {
                issues.Add($"Answer options for {element.Label} are incomplete; repair the definition before running scenarios.");
                continue;
            }
            if (question.AnswerType != saved.AnswerType)
            {
                issues.Add($"The answer type for {element.Label} changed; review this scenario input.");
                continue;
            }
            var selectedOptionIds = new List<int>();
            var selectedKeys = saved.SelectedOptionKeys ?? [];
            if (selectedKeys.Count > PreviewScenarioLimits.MaximumAnswers
                || selectedKeys.Any(key => key == Guid.Empty)
                || selectedKeys.Distinct().Count() != selectedKeys.Count)
            {
                issues.Add($"Saved options for {element.Label} are missing, duplicated, or exceed the supported limit.");
                continue;
            }
            if (question.AnswerType == AnswerType.UploadDocument
                && (!string.IsNullOrEmpty(saved.Text) || !string.IsNullOrEmpty(saved.Number) || selectedKeys.Count > 0))
            {
                issues.Add("Uploaded files are not supported in preview scenarios.");
                continue;
            }
            if (question.AnswerType is not (AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes) && selectedKeys.Count > 0)
            {
                issues.Add($"Saved selections do not match the answer type for {element.Label}.");
                continue;
            }
            if (saved.Number is { Length: > 128 }
                || !string.IsNullOrEmpty(saved.Number)
                    && !decimal.TryParse(saved.Number, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                issues.Add($"Saved numeric input for {element.Label} is invalid; review this scenario input.");
                continue;
            }
            foreach (var optionKey in selectedKeys)
            {
                var matches = question.Options.Where(candidate => candidate.LogicalKey == optionKey).ToArray();
                if (matches.Length != 1)
                {
                    issues.Add(matches.Length == 0
                        ? $"An answer option for {element.Label} no longer exists; review this scenario input."
                        : $"An answer option for {element.Label} is ambiguous; repair this scenario input.");
                    continue;
                }
                selectedOptionIds.Add(matches[0].Id);
            }
            var answerOptionIds = question.AnswerType is AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes
                ? selectedOptionIds : null;
            answers[question.Id] = new PreviewScenarioInput(saved.Text, saved.Number, answerOptionIds);
        }
        return answers;
    }

    private static IReadOnlyList<Guid> CaptureSelectedOptions(Question question, IReadOnlyList<int>? optionIds)
    {
        if (optionIds is null || optionIds.Count == 0) return [];
        if (optionIds.Count > PreviewScenarioLimits.MaximumAnswers)
            throw new DefinitionValidationException("A scenario answer contains too many selected options.");
        var optionGroups = question.Options.GroupBy(option => option.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var logicalKeyGroups = question.Options.Where(option => option.LogicalKey != Guid.Empty)
            .GroupBy(option => option.LogicalKey).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var keys = new List<Guid>(optionIds.Count);
        foreach (var optionId in optionIds.Distinct())
        {
            if (!optionGroups.TryGetValue(optionId, out var matches) || matches.Length != 1
                || matches[0].LogicalKey == Guid.Empty || logicalKeyGroups.Contains(matches[0].LogicalKey))
                throw new DefinitionValidationException("A scenario answer references an option outside its question.");
            keys.Add(matches[0].LogicalKey);
        }
        return keys;
    }

    private static PreviewScenarioPayload? ReadPayload(PreviewScenario scenario, out string? issue)
    {
        if (string.IsNullOrEmpty(scenario.PayloadJson)
            || Encoding.UTF8.GetByteCount(scenario.PayloadJson) > PreviewScenarioLimits.MaximumPayloadBytes)
        {
            issue = "Scenario data is empty or exceeds the supported size; edit or delete this scenario.";
            return null;
        }
        try
        {
            var payload = JsonSerializer.Deserialize<PreviewScenarioPayload>(scenario.PayloadJson, JsonOptions);
            if (payload is null) { issue = "Scenario payload is empty; edit or delete this scenario."; return null; }
            ValidateExpectations(payload.ExpectedValid, payload.ExpectedTotal);
            issue = null;
            return payload;
        }
        catch (Exception exception) when (exception is JsonException or DefinitionValidationException)
        {
            issue = "Scenario data is incomplete or unsupported; edit or delete this scenario.";
            return null;
        }
    }

    private static PreviewScenarioSummary ToSummary(PreviewScenario row)
    {
        var payload = ReadPayload(row, out _);
        return new(row.Id, row.Name, row.RowVersion.ToArray(), row.UpdatedUtc,
            payload?.ExpectedValid, payload?.ExpectedTotal);
    }

    private static PreviewScenarioRunResult NeedsRepair(PreviewScenario scenario, bool? expectedValid,
        int? expectedTotal, IReadOnlyList<string> messages) => new(scenario.Id, scenario.Name,
        scenario.RowVersion.ToArray(), PreviewScenarioRunStatus.NeedsRepair, expectedValid, expectedTotal,
        null, null, messages);

    private static Dictionary<int, Element> ElementsByQuestionId(ApplicationVersionGraph graph) => graph.Categories
        .SelectMany(category => category.Elements).Where(element => element.Question is not null)
        .GroupBy(element => element.Question!.Id).ToDictionary(group => group.Key, group => group.First());

    private static void EnsureDraft(DraftSnapshot snapshot, PreviewScenarioDraft request)
    {
        if (request.ExpectedRevision is null || snapshot.Definition.Version.Status != VersionStatus.Draft
            || snapshot.Definition.Version.Id != request.ExpectedVersionId
            || !snapshot.Revision.AsSpan().SequenceEqual(request.ExpectedRevision))
            throw new DraftConflictException();
    }

    private static void ValidateExpectations(bool? expectedValid, int? expectedTotal)
    {
        if (expectedValid is null && expectedTotal is null)
            throw new DefinitionValidationException("Set at least one expected result: answer validity or total points.");
        if (expectedTotal < 0)
            throw new DefinitionValidationException("Expected total points cannot be negative.");
    }

    private static string Word(bool value) => value ? "valid" : "invalid";

    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() }
    };
}
