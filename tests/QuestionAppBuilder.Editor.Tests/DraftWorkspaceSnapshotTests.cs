using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Editor.Controllers;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.Editor.ViewModels;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class DraftWorkspaceSnapshotTests
{
    [Fact]
    public async Task Index_uses_one_workspace_result_for_the_graph_revision_and_readiness()
    {
        var graph = new ApplicationVersionGraph { Version = new ApplicationVersion { ApplicationId = 73 }, Categories = [] };
        var revision = new byte[] { 4, 5, 6 };
        var draft = new DraftSnapshot(graph, revision);
        var review = new PublicationReview(revision,
            [new PublicationIssue("definition-error", 9, "Fix this definition.")],
            [new PublicationIssue("definition-warning", null, "Review this warning.")]);
        var drafts = new WorkspaceDraftService(new DraftWorkspaceSnapshot(draft, review));
        var controller = new DraftsController(new QuestionAppBuilderEditorOptions(), drafts, applications: null!);

        var result = await controller.Index(73, CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<DraftWorkspaceViewModel>().Subject;
        model.Graph.Should().BeSameAs(graph);
        model.Revision.Should().BeSameAs(revision);
        model.Errors.Should().ContainSingle().Which.Message.Should().Be("Fix this definition.");
        model.Warnings.Should().ContainSingle().Which.Message.Should().Be("Review this warning.");
        drafts.WorkspaceCalls.Should().Be(1);
        drafts.GetCalls.Should().Be(0);
        drafts.ReviewCalls.Should().Be(0);
    }

    [Fact]
    public async Task Default_workspace_operation_preserves_compatibility_for_legacy_implementations()
    {
        var graph = new ApplicationVersionGraph { Version = new ApplicationVersion { ApplicationId = 73 }, Categories = [] };
        var revision = new byte[] { 7, 8, 9 };
        var draft = new DraftSnapshot(graph, revision);
        var review = new PublicationReview(revision, [], []);
        var legacy = new LegacyDraftService(draft, review);

        var workspace = await ((IDraftService)legacy).GetWorkspaceAsync(73, CancellationToken.None);

        workspace.Draft.Definition.Should().BeSameAs(graph);
        workspace.Draft.Revision.Should().BeSameAs(revision);
        workspace.Review.Revision.Should().BeSameAs(revision);
        legacy.GetCalls.Should().Be(1);
        legacy.ReviewCalls.Should().Be(1);
    }

    private sealed class WorkspaceDraftService(DraftWorkspaceSnapshot workspace) : IDraftService
    {
        public int WorkspaceCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int ReviewCalls { get; private set; }

        public Task<DraftWorkspaceSnapshot> GetWorkspaceAsync(int applicationId, CancellationToken ct = default)
        {
            WorkspaceCalls++;
            return Task.FromResult(workspace);
        }

        public Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(workspace.Draft);
        }

        public Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default)
        {
            ReviewCalls++;
            return Task.FromResult(workspace.Review);
        }

        public Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class LegacyDraftService(DraftSnapshot draft, PublicationReview review) : IDraftService
    {
        public int GetCalls { get; private set; }
        public int ReviewCalls { get; private set; }

        public Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(draft);
        }

        public Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default)
        {
            ReviewCalls++;
            return Task.FromResult(review);
        }
        public Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
