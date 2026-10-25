using System.Text.Json;
using FluentAssertions;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Interfaces;

namespace QuestionAppBuilder.Application.Tests.Authoring;

public class PreviewScenarioBoundaryTests
{
    [Fact]
    public async Task Run_all_uses_one_snapshot_and_matches_preview_for_nonchoice_answers_and_capped_points()
    {
        var graph = Graph();
        var number = graph.Categories[0].Elements[0];
        number.Question!.WholeNumbersOnly = true;
        number.Question.MinValue = 0;
        number.Question.Rules = [new ScoringRule
        {
            Id = 1, Family = RuleFamily.NumericRange, MinValue = 0,
            AwardMethod = AwardMethod.PointsPerUnit, PointsPerUnit = 3, MaximumPoints = 20
        }];
        var text = new Element { Id = 2, Kind = ElementKind.Question, Label = "Explanation",
            Question = new Question { Id = 2, AnswerType = AnswerType.Textbox, IsRequired = true } };
        var date = new Element { Id = 3, Kind = ElementKind.Question, Label = "Date",
            Question = new Question { Id = 3, AnswerType = AnswerType.Date, IsRequired = true } };
        graph.Categories[0].Elements.AddRange([text, date]);
        var rows = new[]
        {
            Row(new(1, [new(number.LogicalKey, AnswerType.Number, null, "9", []),
                new(text.LogicalKey, AnswerType.Textbox, "Test explanation", null, []),
                new(date.LogicalKey, AnswerType.Date, "2026-10-25", null, [])], true, 20)),
            Row(new(1, [], false, 0))
        };
        var (service, drafts, request) = Service(graph, rows);

        var report = await service.RunAsync(request);

        report.Results.Select(result => result.Status).Should().Equal(PreviewScenarioRunStatus.Passed, PreviewScenarioRunStatus.Passed);
        report.Results.Select(result => result.ActualValid).Should().Equal(true, false);
        report.Results.Select(result => result.ActualTotal).Should().Equal(20, 0);
        drafts.ReadCount.Should().Be(1);
    }

    [Theory]
    [InlineData("duplicate-answer")]
    [InlineData("duplicate-element")]
    [InlineData("changed-type")]
    [InlineData("duplicate-option")]
    [InlineData("deleted-option")]
    [InlineData("file-token")]
    [InlineData("unsupported-schema")]
    public async Task Ambiguous_or_incompatible_inputs_need_repair_and_cannot_pass_expected_invalid(string problem)
    {
        var graph = Graph();
        var element = graph.Categories[0].Elements[0];
        var answer = new PreviewScenarioAnswer(element.LogicalKey, AnswerType.Number, null, "3", []);
        IReadOnlyList<PreviewScenarioAnswer> answers = [answer];
        if (problem == "duplicate-answer") answers = [answer, answer];
        if (problem == "duplicate-element")
            graph.Categories[0].Elements.Add(new Element { Id = 2, LogicalKey = element.LogicalKey,
                Kind = ElementKind.Question, Question = new Question { Id = 2, AnswerType = AnswerType.Number } });
        if (problem == "changed-type") element.Question!.AnswerType = AnswerType.Textbox;
        if (problem is "duplicate-option" or "deleted-option")
        {
            element.Question!.AnswerType = AnswerType.Checkboxes;
            var optionKey = Guid.NewGuid();
            element.Question.Options = problem == "deleted-option" ? [] :
                [new QuestionOption { Id = 1, LogicalKey = optionKey }, new QuestionOption { Id = 2, LogicalKey = optionKey }];
            answers = [new(element.LogicalKey, AnswerType.Checkboxes, null, null, [optionKey])];
        }
        if (problem == "file-token")
        {
            element.Question!.AnswerType = AnswerType.UploadDocument;
            answers = [new(element.LogicalKey, AnswerType.UploadDocument, "stored-document-token", null, [])];
        }
        var row = Row(new(problem == "unsupported-schema" ? 99 : 1, answers, false, null));
        var (service, _, request) = Service(graph, [row]);

        var result = (await service.RunAsync(request)).Results.Single();
        result.Status.Should().Be(PreviewScenarioRunStatus.NeedsRepair);
        result.ActualValid.Should().BeNull();
        result.ActualTotal.Should().BeNull();
        result.Messages.Should().NotBeEmpty();
        var loaded = await service.LoadAsync(request, row.Id);
        loaded.RepairIssues.Should().NotBeEmpty();
        loaded.Answers.Should().BeEmpty("a repair must never silently load a partial input set");
    }

    [Fact]
    public async Task Definition_errors_cannot_satisfy_an_expected_invalid_answer_result()
    {
        var graph = Graph();
        graph.Categories[0].Elements[0].VisibilityConditionId = 999;
        var (service, _, request) = Service(graph, [Row(new(1, [], false, 0))]);

        var result = (await service.RunAsync(request)).Results.Single();

        result.Status.Should().Be(PreviewScenarioRunStatus.NeedsRepair);
        result.ActualValid.Should().BeNull();
        result.Messages.Should().Contain(message => message.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    private static PreviewScenario Row(PreviewScenarioPayload payload) => new()
    {
        ApplicationId = 7, Name = "Test case", RowVersion = [1], PayloadJson = JsonSerializer.Serialize(payload)
    };

    private static (PreviewScenarioService Service, DraftStub Drafts, PreviewScenarioDraft Request)
        Service(ApplicationVersionGraph graph, IReadOnlyList<PreviewScenario> rows)
    {
        var snapshot = new DraftSnapshot(graph, graph.Version.RevisionNonce.ToByteArray());
        var drafts = new DraftStub(snapshot);
        return (new PreviewScenarioService(drafts, new ReadOnlyScenarioStub(rows), new DefinitionEvaluator()), drafts,
            new PreviewScenarioDraft(7, 11, snapshot.Revision));
    }

    private sealed class DraftStub(DraftSnapshot snapshot) : IDraftService
    {
        public int ReadCount { get; private set; }
        public Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default)
        {
            applicationId.Should().Be(7);
            ReadCount++;
            return Task.FromResult(snapshot);
        }
        public Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default) => throw new InvalidOperationException("A scenario run must not write the draft.");
        public Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default) => throw new InvalidOperationException("A scenario run must not publish.");
    }

    private sealed class ReadOnlyScenarioStub(IReadOnlyList<PreviewScenario> rows) : IPreviewScenarioRepository
    {
        public Task<IReadOnlyList<PreviewScenario>> ListAsync(int applicationId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PreviewScenario>>(rows.Where(row => row.ApplicationId == applicationId).ToArray());
        public Task<PreviewScenario?> GetAsync(int applicationId, Guid scenarioId, CancellationToken ct = default)
            => Task.FromResult(rows.SingleOrDefault(row => row.ApplicationId == applicationId && row.Id == scenarioId));
        public Task<PreviewScenario> SaveAsync(PreviewScenario scenario, int expectedVersionId, byte[] expectedDraftRevision,
            byte[]? expectedScenarioRevision, CancellationToken ct = default) => throw new InvalidOperationException("Running must not save a scenario.");
        public Task DeleteAsync(int applicationId, Guid scenarioId, byte[] expectedScenarioRevision, CancellationToken ct = default)
            => throw new InvalidOperationException("Running must not delete a scenario.");
    }

    private static ApplicationVersionGraph Graph() => new()
    {
        Version = new ApplicationVersion { Id = 11, ApplicationId = 7, Status = VersionStatus.Draft, RevisionNonce = Guid.NewGuid() },
        Categories = [new Category { Id = 1, Name = "Assessment", Elements =
            [new Element { Id = 1, Kind = ElementKind.Question, Label = "Participants",
                Question = new Question { Id = 1, AnswerType = AnswerType.Number, IsRequired = true } }] }]
    };
}
