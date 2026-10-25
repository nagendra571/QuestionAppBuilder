using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Editor.Controllers;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class DefinitionDependenciesControllerTests
{
    [Fact]
    public async Task Dependencies_reads_one_saved_snapshot_and_rejects_a_stale_revision()
    {
        var element = new Element { Id = 8, CategoryId = 2, Kind = QuestionAppBuilder.Domain.Enums.ElementKind.Content, Label = "Guidance" };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { ApplicationId = 73, CompositionJson = "{}" },
            Categories = [new Category { Id = 2, Name = "Intro", Elements = [element] }]
        };
        var revision = new byte[] { 4, 5, 6 };
        var drafts = new SnapshotDraftService(new DraftSnapshot(graph, revision));
        var controller = new DraftsController(new QuestionAppBuilderEditorOptions(), drafts, applications: null!);

        var success = await controller.Dependencies(73, element.LogicalKey, Convert.ToBase64String(revision), CancellationToken.None);
        var response = success.Should().BeOfType<JsonResult>().Subject.Value.Should().BeOfType<DefinitionDependencyResponse>().Subject;
        response.Revision.Should().Be(Convert.ToBase64String(revision));
        response.Report.Complete.Should().BeTrue();
        drafts.GetCalls.Should().Be(1);
        typeof(DraftsController).GetMethod(nameof(DraftsController.Dependencies))!.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore.Should().BeTrue();

        var missing = await controller.Dependencies(73, Guid.NewGuid(), Convert.ToBase64String(revision), CancellationToken.None);
        missing.Should().BeOfType<NotFoundResult>();
        drafts.GetCalls.Should().Be(2);

        var stale = await controller.Dependencies(73, element.LogicalKey, Convert.ToBase64String([99]), CancellationToken.None);
        stale.Should().BeOfType<ConflictObjectResult>();
        drafts.GetCalls.Should().Be(3);

        var invalid = await controller.Dependencies(73, element.LogicalKey, "not-base64", CancellationToken.None);
        invalid.Should().BeOfType<BadRequestResult>();
        drafts.GetCalls.Should().Be(3);
    }

    private sealed class SnapshotDraftService(DraftSnapshot snapshot) : IDraftService
    {
        public int GetCalls { get; private set; }
        public Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default) { GetCalls++; return Task.FromResult(snapshot); }
        public Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
