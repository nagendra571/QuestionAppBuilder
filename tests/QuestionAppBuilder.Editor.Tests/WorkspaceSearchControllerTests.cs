using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Editor.Controllers;
using QuestionAppBuilder.Editor.Options;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class WorkspaceSearchControllerTests
{
    [Fact]
    public async Task Search_reads_one_revision_pinned_draft_and_maps_safe_destinations()
    {
        var element = new Element { Id = 8, CategoryId = 4, Kind = QuestionAppBuilder.Domain.Enums.ElementKind.Content, Label = "Budget narrative" };
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 91, ApplicationId = 73, CompositionJson = "{}" },
            Categories = [new Category { Id = 4, Name = "Budget", Elements = [element] }]
        };
        var revision = new byte[] { 4, 5, 6 };
        var drafts = new SnapshotDraftService(new DraftSnapshot(graph, revision));
        var controller = CreateController(drafts);

        var result = await controller.Search(73, "budget", Convert.ToBase64String(revision), CancellationToken.None);

        var json = result.Should().BeOfType<JsonResult>().Subject;
        var response = json.Value.Should().BeAssignableTo<object>().Subject;
        var serialized = System.Text.Json.JsonSerializer.SerializeToElement(response, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        serialized.GetProperty("versionId").GetInt32().Should().Be(91);
        serialized.GetProperty("revision").GetString().Should().Be(Convert.ToBase64String(revision));
        serialized.GetProperty("items")[0].GetProperty("url").GetString().Should().Be("/ConfigureApp/Drafts/73#category-4");
        drafts.GetCalls.Should().Be(1);
        typeof(WorkspaceSearchController).GetMethod(nameof(WorkspaceSearchController.Search))!
            .GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: true).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Search_rejects_invalid_query_or_stale_revision_before_searching()
    {
        var graph = new ApplicationVersionGraph { Version = new ApplicationVersion { Id = 91, ApplicationId = 73 }, Categories = [] };
        var revision = new byte[] { 4, 5, 6 };
        var drafts = new SnapshotDraftService(new DraftSnapshot(graph, revision));
        var controller = CreateController(drafts);

        (await controller.Search(73, "  ", Convert.ToBase64String(revision), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Search(73, new string('x', 201), Convert.ToBase64String(revision), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Search(73, "budget", "invalid", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        drafts.GetCalls.Should().Be(0);

        var stale = await controller.Search(73, "budget", Convert.ToBase64String([99]), CancellationToken.None);
        stale.Should().BeOfType<ConflictObjectResult>();
        drafts.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task Search_accepts_minimum_query_length_and_caps_reference_results_at_report_limit()
    {
        var graph = new ApplicationVersionGraph
        {
            Version = new ApplicationVersion { Id = 91, ApplicationId = 73 },
            Categories = [new Category { Id = 4, Name = "A", Elements = [] }]
        };
        var drafts = new SnapshotDraftService(new DraftSnapshot(graph, [1]));
        var controller = CreateController(drafts);

        var result = await controller.Search(73, " A ", Convert.ToBase64String([1]), CancellationToken.None);

        result.Should().BeOfType<JsonResult>();
        drafts.GetCalls.Should().Be(1);
    }

    private static WorkspaceSearchController CreateController(IDraftService drafts)
    {
        var controller = new WorkspaceSearchController(new QuestionAppBuilderEditorOptions(), drafts)
        {
            Url = new SearchUrlHelper()
        };
        return controller;
    }

    private sealed class SnapshotDraftService(DraftSnapshot snapshot) : IDraftService
    {
        public int GetCalls { get; private set; }
        public Task<DraftSnapshot> GetAsync(int applicationId, CancellationToken ct = default) { GetCalls++; return Task.FromResult(snapshot); }
        public Task<DraftSnapshot> SaveAsync(SaveDraftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PublicationReview> ReviewAsync(int applicationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PublishAsync(PublishRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class SearchUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext)
        {
            var values = new RouteValueDictionary(actionContext.Values);
            var applicationId = values.TryGetValue("applicationId", out var workspaceId) ? workspaceId
                : values.TryGetValue("id", out var compositionId) ? compositionId : null;
            return actionContext.Controller == "Drafts" ? $"/ConfigureApp/Drafts/{applicationId}"
                : actionContext.Controller == "Composition" ? $"/ConfigureApp/Composition/Index/{applicationId}" : null;
        }
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => url?.StartsWith('/') == true && !url.StartsWith("//", StringComparison.Ordinal);
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
