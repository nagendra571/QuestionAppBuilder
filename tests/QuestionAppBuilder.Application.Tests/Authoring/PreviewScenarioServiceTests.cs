using FluentAssertions;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Interfaces;

namespace QuestionAppBuilder.Application.Tests.Authoring;

public sealed class PreviewScenarioServiceTests
{
    [Fact]
    public async Task Saved_scenario_resolves_stable_element_keys_after_a_draft_clone_and_runs_without_writes()
    {
        var graph = BuildGraph();
        var revision = graph.Version.RevisionNonce.ToByteArray();
        var drafts = new DraftServiceStub(new DraftSnapshot(graph, revision));
        var scenarios = new ScenarioRepositoryStub();
        var service = new PreviewScenarioService(drafts, scenarios, new DefinitionEvaluator());

        var saved = await service.SaveAsync(new SavePreviewScenarioRequest(
            new PreviewScenarioDraft(7, 11, revision), null, null, "  Ten is valid  ",
            new Dictionary<int, PreviewScenarioInput> { [21] = new(null, "10.0", null) },
            ExpectedValid: true, ExpectedTotal: 4));
        saved.Name.Should().Be("Ten is valid");
        var persisted = scenarios.Rows.Should().ContainSingle().Subject;
        persisted.PayloadJson.Should().Contain(graph.Categories[0].Elements[0].LogicalKey.ToString());
        persisted.PayloadJson.Should().NotContain("\"21\"");

        var (clone, map) = DefinitionCloner.Clone(graph, versionNumber: 2);
        clone.Version.Id = 12;
        clone.Version.RevisionNonce = Guid.NewGuid();
        var clonedElement = map.Elements[31];
        clonedElement.Id = 231;
        clonedElement.CategoryId = 221;
        clonedElement.Question!.Id = 231;
        map.Categories[41].Id = 221;
        drafts.Snapshot = new DraftSnapshot(clone, clone.Version.RevisionNonce.ToByteArray());

        var report = await service.RunAsync(new PreviewScenarioDraft(7, 12, drafts.Snapshot.Revision));
        report.VersionId.Should().Be(12);
        report.Results.Should().ContainSingle();
        report.Results[0].Status.Should().Be(PreviewScenarioRunStatus.Passed);
        report.Results[0].ActualValid.Should().BeTrue();
        report.Results[0].ActualTotal.Should().Be(4);
        drafts.ReadCount.Should().Be(2);
    }

    [Fact]
    public async Task Missing_question_key_requires_repair_instead_of_silently_dropping_the_answer()
    {
        var graph = BuildGraph();
        var revision = graph.Version.RevisionNonce.ToByteArray();
        var drafts = new DraftServiceStub(new DraftSnapshot(graph, revision));
        var scenarios = new ScenarioRepositoryStub();
        var service = new PreviewScenarioService(drafts, scenarios, new DefinitionEvaluator());
        await service.SaveAsync(new SavePreviewScenarioRequest(new(7, 11, revision), null, null,
            "Answer saved", new Dictionary<int, PreviewScenarioInput> { [21] = new(null, "10", null) },
            ExpectedValid: true, ExpectedTotal: 4));

        var changed = BuildGraph();
        changed.Categories[0].Elements[0].LogicalKey = Guid.NewGuid();
        changed.Version.RevisionNonce = Guid.NewGuid();
        drafts.Snapshot = new DraftSnapshot(changed, changed.Version.RevisionNonce.ToByteArray());

        var report = await service.RunAsync(new PreviewScenarioDraft(7, 11, drafts.Snapshot.Revision));
        report.Results.Should().ContainSingle();
        report.Results[0].Status.Should().Be(PreviewScenarioRunStatus.NeedsRepair);
        report.Results[0].ActualValid.Should().BeNull();
        report.Results[0].ActualTotal.Should().BeNull();
        report.Results[0].Messages.Should().ContainSingle().Which.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task Selected_option_is_rebound_by_logical_key_after_a_clone_changes_database_ids()
    {
        var graph = BuildGraph();
        var question = graph.Categories[0].Elements[0].Question!;
        question.AnswerType = AnswerType.Checkboxes;
        var option = new QuestionOption { Id = 55, QuestionId = question.Id, Label = "Ready", Value = "ready", LogicalKey = Guid.NewGuid() };
        question.Options.Add(option);
        var revision = graph.Version.RevisionNonce.ToByteArray();
        var drafts = new DraftServiceStub(new DraftSnapshot(graph, revision));
        var scenarios = new ScenarioRepositoryStub();
        var service = new PreviewScenarioService(drafts, scenarios, new DefinitionEvaluator());
        await service.SaveAsync(new SavePreviewScenarioRequest(new(7, 11, revision), null, null,
            "Selected option", new Dictionary<int, PreviewScenarioInput> { [question.Id] = new(null, null, [option.Id]) },
            ExpectedValid: true, ExpectedTotal: 0));

        var (clone, map) = DefinitionCloner.Clone(graph, versionNumber: 2);
        clone.Version.Id = 12;
        clone.Version.RevisionNonce = Guid.NewGuid();
        var clonedQuestion = map.Questions[question.Id];
        var clonedElement = map.Elements[31];
        map.Categories[41].Id = 221;
        clonedElement.Id = 231; clonedElement.CategoryId = 221;
        clonedQuestion.Id = 231;
        var clonedOption = map.Options[option.Id]; clonedOption.Id = 555; clonedOption.QuestionId = 231;
        drafts.Snapshot = new DraftSnapshot(clone, clone.Version.RevisionNonce.ToByteArray());

        var report = await service.RunAsync(new PreviewScenarioDraft(7, 12, drafts.Snapshot.Revision));

        report.Results.Should().ContainSingle();
        report.Results[0].Status.Should().Be(PreviewScenarioRunStatus.Passed);
        report.Results[0].ActualValid.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_requires_an_expectation_and_rejects_unrepresented_file_answers()
    {
        var graph = BuildGraph();
        var revision = graph.Version.RevisionNonce.ToByteArray();
        var service = new PreviewScenarioService(new DraftServiceStub(new DraftSnapshot(graph, revision)),
            new ScenarioRepositoryStub(), new DefinitionEvaluator());

        var missingExpectation = () => service.SaveAsync(new SavePreviewScenarioRequest(new(7, 11, revision), null, null,
            "Missing expectation", new Dictionary<int, PreviewScenarioInput>(), null, null));
        await missingExpectation.Should().ThrowAsync<QuestionAppBuilder.Domain.Exceptions.DefinitionValidationException>()
            .WithMessage("Set at least one expected result: answer validity or total points.");

        var file = new Element { Id = 42, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Question, Label = "Attachment",
            Question = new Question { Id = 42, AnswerType = AnswerType.UploadDocument } };
        graph.Categories[0].Elements.Add(file);
        var fileScenario = () => service.SaveAsync(new SavePreviewScenarioRequest(new(7, 11, revision), null, null,
            "File answer", new Dictionary<int, PreviewScenarioInput> { [42] = new("document.pdf", null, null) },
            ExpectedValid: false, ExpectedTotal: null));
        await fileScenario.Should().ThrowAsync<QuestionAppBuilder.Domain.Exceptions.DefinitionValidationException>()
            .WithMessage("Uploaded files are not supported in preview scenarios; clear the file input and save an empty answer.");
    }

    private static ApplicationVersionGraph BuildGraph()
    {
        var question = new Question { Id = 21, AnswerType = AnswerType.Number, IsRequired = true, MinValue = 0, MaxValue = 100 };
        question.Rules.Add(new ScoringRule { Id = 51, QuestionId = 21, Family = RuleFamily.NumericRange, MinValue = 0, MaxValue = 20, Points = 4 });
        var element = new Element { Id = 31, CategoryId = 41, LogicalKey = Guid.NewGuid(), Kind = ElementKind.Question,
            Label = "Amount", Question = question };
        var category = new Category { Id = 41, Name = "Funding", Elements = { element } };
        return new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 11, ApplicationId = 7, VersionNumber = 1, Status = VersionStatus.Draft,
                RevisionNonce = Guid.NewGuid(), CompositionJson = "{}" },
            Categories = [category]
        };
    }

    private sealed class DraftServiceStub(DraftSnapshot snapshot) : IDraftService
    {
        public DraftSnapshot Snapshot { get; set; } = snapshot;
        public int ReadCount { get; private set; }
        public Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default)
        {
            ReadCount++;
            if (applicationId != Snapshot.Definition.Version.ApplicationId) throw new InvalidOperationException("wrong app");
            return Task.FromResult(Snapshot);
        }
        public Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ScenarioRepositoryStub : IPreviewScenarioRepository
    {
        public List<PreviewScenario> Rows { get; } = [];
        public Task<IReadOnlyList<PreviewScenario>> ListAsync(int applicationId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PreviewScenario>>(Rows.Where(row => row.ApplicationId == applicationId).ToArray());
        public Task<PreviewScenario?> GetAsync(int applicationId, Guid scenarioId, CancellationToken ct = default)
            => Task.FromResult(Rows.FirstOrDefault(row => row.ApplicationId == applicationId && row.Id == scenarioId));
        public Task<PreviewScenario> SaveAsync(PreviewScenario scenario, int expectedVersionId, byte[] expectedDraftRevision,
            byte[]? expectedScenarioRevision, CancellationToken ct = default)
        {
            var row = new PreviewScenario
            {
                Id = scenario.Id, ApplicationId = scenario.ApplicationId, Name = scenario.Name,
                PayloadJson = scenario.PayloadJson, CreatedUtc = scenario.CreatedUtc, UpdatedUtc = scenario.UpdatedUtc
            };
            row.RowVersion = [1, 2, 3];
            Rows.RemoveAll(existing => existing.Id == row.Id);
            Rows.Add(row);
            return Task.FromResult(row);
        }
        public Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision, CancellationToken ct = default)
        {
            Rows.RemoveAll(row => row.ApplicationId == applicationId && row.Id == scenarioId);
            return Task.CompletedTask;
        }
    }
}
