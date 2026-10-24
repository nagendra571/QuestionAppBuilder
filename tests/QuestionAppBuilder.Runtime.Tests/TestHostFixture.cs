using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Core.DependencyInjection;
using QuestionAppBuilder.Core.Routing;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class TestHostFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public FakeRuntime Runtime { get; }

    private TestHostFixture(WebApplication app, FakeRuntime runtime)
    { _app = app; Client = app.GetTestClient(); Runtime = runtime; }

    public static async Task<TestHostFixture> StartAsync(FakeRuntime? runtime = null)
    {
        runtime ??= new FakeRuntime();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuestionAppBuilderRuntime(options =>
        { options.FillPolicy = "Fill"; options.BasePath = "/App"; options.LayoutName = null!; options.PublicBaseUri = new Uri("https://forms.example.test"); });
        builder.Services.AddAuthorization(options => options.AddPolicy("Fill", policy => policy.RequireAssertion(_ => true)));
        builder.Services.AddSingleton<IDefinitionEvaluator>(new DefinitionEvaluator());
        builder.Services.AddSingleton<IResponseService>(runtime);
        builder.Services.AddSingleton<ISubmissionCoordinator>(runtime);
        builder.Services.AddSingleton<IResponseAccessService>(runtime);
        var app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
        app.MapQuestionAppBuilderRuntime();
        await app.StartAsync();
        return new TestHostFixture(app, runtime);
    }

    public ValueTask DisposeAsync() { Client.Dispose(); return _app.DisposeAsync(); }

    public sealed class FakeRuntime : IResponseService, ISubmissionCoordinator, IResponseAccessService
    {
        private readonly Dictionary<Guid, ResponseSnapshot> _responses = new();
        private readonly Dictionary<Guid, SubmissionReceipt> _receipts = new();
        private readonly Dictionary<Guid, Guid> _requestResponses = new();
        private int _revision;
        public ApplicationVersionGraph Graph { get; set; } = BuildGraph();
        public bool FailNextSave { get; set; }
        public bool AcceptingNewResponses { get; set; } = true;
        public bool AcceptingSubmissions { get; set; } = true;
        public int SubmissionCount { get; private set; }
        public IReadOnlyDictionary<int, AnswerValue> SubmittedAnswers { get; private set; } = new Dictionary<int, AnswerValue>();
        public static Guid ResponseId { get; } = Guid.Parse("9b91b90c-0cb7-40b4-b185-57d99bcb31a2");

        public Task<ResponseLandingSnapshot?> GetLandingAsync(int applicationId, CancellationToken ct = default)
            => Task.FromResult<ResponseLandingSnapshot?>(new ResponseLandingSnapshot(applicationId, "Test application", Graph, AcceptingNewResponses, AcceptingSubmissions));

        public Task<ResponseSnapshot> StartAsync(int applicationId, CancellationToken ct = default)
        {
            var response = new ResponseSnapshot(ResponseId, applicationId, Graph.Version.Id, Graph,
                new Dictionary<int, AnswerValue>(), ResponseStatus.InProgress, RevisionToken(), DateTime.UtcNow, AcceptingNewResponses, AcceptingSubmissions);
            _responses[ResponseId] = response;
            return Task.FromResult(response);
        }
        public Task<ResponseSnapshot> GetAsync(Guid responseId, CancellationToken ct = default)
            => Task.FromResult(_responses.TryGetValue(responseId, out var response) ? response : throw new NotFoundException("missing"));
        public Task<ResponseSnapshot> SaveAsync(SaveResponseRequest request, CancellationToken ct = default)
        {
            if (FailNextSave) { FailNextSave = false; throw new ResponseConflictException(); }
            var current = _responses[request.ResponseId];
            if (!current.Revision.SequenceEqual(request.ExpectedRevision)) throw new ResponseConflictException();
            var updated = current with { Answers = new Dictionary<int, AnswerValue>(request.Answers), Revision = RevisionToken(), UpdatedUtc = DateTime.UtcNow };
            _responses[request.ResponseId] = updated;
            return Task.FromResult(updated);
        }
        public Task<IReadOnlyList<ResponseSummary>> ListMineAsync(int take, DateTime? beforeUtc = null, Guid? beforeId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ResponseSummary>>(_responses.Values.Select(r => new ResponseSummary(r.Id, r.ApplicationId, r.ApplicationVersionId, Graph.Version.DisplayName, r.UpdatedUtc)).ToArray());

        public Task<SubmissionReceipt> SubmitAsync(SubmitResponseRequest request, CancellationToken cancellationToken = default)
        {
            if (!AcceptingSubmissions) return Task.FromException<SubmissionReceipt>(new InvalidOperationException("This questionnaire is not accepting submissions."));
            if (_receipts.TryGetValue(request.RequestId, out var prior)) return Task.FromResult(prior);
            var snapshot = _responses[request.ResponseId];
            var result = new DefinitionEvaluator().Evaluate(snapshot.Definition, snapshot.Answers, true);
            if (result.AnswerIssues.Count > 0) throw new AnswerValidationException("Required answers are missing.", result.AnswerIssues.Where(x => x.QuestionId.HasValue).Select(x => x.QuestionId!.Value).ToArray());
            SubmittedAnswers = snapshot.Answers.Where(answer => result.VisibleQuestionIds.Contains(answer.Key)).ToDictionary(x => x.Key, x => x.Value);
            SubmissionCount++;
            var receipt = new SubmissionReceipt(SubmissionCount, request.ResponseId, DateTime.UtcNow, Graph.Version.ConfirmationText,
                Graph.Version.ResultVisibility == ResultVisibility.ConfirmationOnly ? null : result.Score.Total,
                Graph.Version.ResultVisibility == ResultVisibility.CategoryBreakdown ? result.Score.Categories : null);
            if (Graph.Version.ResultVisibility == ResultVisibility.ConfirmationAndAnswerReview)
            {
                receipt = receipt with { Total = 2, Categories = null,
                    SubmittedApplication = BuildSubmittedApplication(snapshot, result, withResults: true) };
            }
            _responses[request.ResponseId] = snapshot with { Status = ResponseStatus.Submitted };
            _receipts[request.RequestId] = receipt; _requestResponses[request.RequestId] = request.ResponseId;
            return Task.FromResult(receipt);
        }
        public Task<SubmissionReceipt> GetReceiptAsync(Guid responseId, CancellationToken cancellationToken = default)
        {
            var receipt = _receipts.FirstOrDefault(x => _requestResponses[x.Key] == responseId).Value;
            return receipt is null ? Task.FromException<SubmissionReceipt>(new NotFoundException("missing")) : Task.FromResult(receipt);
        }
        public async Task<SubmissionReceipt> GetSubmittedApplicationAsync(Guid responseId, CancellationToken cancellationToken = default)
        {
            var receipt = await GetReceiptAsync(responseId, cancellationToken);
            var snapshot = _responses[responseId];
            var result = new DefinitionEvaluator().Evaluate(snapshot.Definition, snapshot.Answers, requireComplete: true);
            var withScores = Graph.Version.SubmittedApplicationVisibility == SubmittedApplicationVisibility.WithScores;
            return receipt with { Total = withScores ? 2 : null, Categories = null,
                SubmittedApplication = BuildSubmittedApplication(snapshot, result, withScores) };
        }
        public Task RequestAsync(AccessLinkRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AccessGrant> RedeemAsync(string token, CancellationToken cancellationToken = default) => Task.FromException<AccessGrant>(new InvalidAccessLinkException());

        private byte[] RevisionToken() => BitConverter.GetBytes(Interlocked.Increment(ref _revision));

        public static ApplicationVersionGraph BuildGraph(NavigationMode mode = NavigationMode.Sequential, ResultVisibility visibility = ResultVisibility.Total, bool conditional = false)
        {
            var version = new ApplicationVersion { Id = 10, ApplicationId = 1, VersionNumber = 1, Status = VersionStatus.Published,
                DisplayName = "Test questionnaire", NavigationMode = mode, ResultVisibility = visibility,
                SubmittedApplicationVisibility = SubmittedApplicationVisibility.WithoutScores,
                SubmitLabel = "Send application", ConfirmationText = "Thanks for your response" };
            var categories = new List<Category>();
            for (var i = 1; i <= 5; i++)
            {
                var question = new Question { Id = i, AnswerType = AnswerType.Textbox, IsRequired = true };
                var element = new Element { Id = i, Kind = ElementKind.Question, Label = $"Question {i}", SortOrder = 1, LogicalKey = Guid.NewGuid(), Question = question };
                var category = new Category { Id = i, Name = $"Category {i}", SortOrder = i, Elements = { element } };
                categories.Add(category);
            }
            var conditions = new List<VisibilityCondition>();
            if (conditional)
            {
                categories[0].Elements[0].Question!.IsRequired = false;
                var condition = new VisibilityCondition { Id = 10, ApplicationVersionId = version.Id, Junction = ConditionJunction.All,
                    Predicates = { new VisibilityPredicate { Id = 1, SourceQuestionId = 1, Operator = ConditionOperator.Answered } } };
                conditions.Add(condition);
                categories[1].Elements[0].VisibilityConditionId = condition.Id;
            }
            version.Categories = categories; version.VisibilityConditions = conditions;
            return new ApplicationVersionGraph { Version = version, Categories = categories, VisibilityConditions = conditions };
        }

        private static SubmittedApplication BuildSubmittedApplication(ResponseSnapshot snapshot,
            QuestionAppBuilder.Application.Evaluation.EvaluationResult result, bool withResults)
        {
            var categories = snapshot.Definition.Categories.OrderBy(category => category.SortOrder).Select(category =>
            {
                var elements = category.Elements.OrderBy(element => element.SortOrder)
                    .Where(element => element.Question is null || result.VisibleQuestionIds.Contains(element.Question.Id))
                    .Select(element => new SubmittedElement(element.Id, element.LogicalKey, element.Kind, element.Label,
                        element.Id == 1 ? "Answer in the indicated field." : element.HelpText, 0, null,
                        element.Question is null ? null : new SubmittedQuestion(element.Question.Id, element.Question.AnswerType,
                            snapshot.Answers.GetValueOrDefault(element.Question.Id)?.Text,
                            withResults && element.Question.Id == 1 ? 1 : null))).ToList();
                if (category.Id == snapshot.Definition.Categories.Min(item => item.Id))
                    elements.Add(new SubmittedElement(100, Guid.NewGuid(), ElementKind.Content, "Application introduction", null, 0, null, null));
                var questionElement = elements.FirstOrDefault(element => element.Question is not null);
                IReadOnlyList<SubmittedGroup> groups = withResults && questionElement is not null
                    && category.Id == snapshot.Definition.Categories.Min(item => item.Id)
                    ? new[] { new SubmittedGroup("Summary", true, ["Details"],
                        [new SubmittedGroupRow(questionElement, ["Primary"])]) }
                    : Array.Empty<SubmittedGroup>();
                return new SubmittedCategory(category.Id, category.Name, elements, groups, [],
                    withResults && category.Id == snapshot.Definition.Categories.Min(item => item.Id) ? 2 : null,
                    withResults && category.Id == snapshot.Definition.Categories.Min(item => item.Id) ? [new SubmittedAward("Supporter", 1)] : []);
            }).ToArray();
            return new SubmittedApplication(snapshot.Definition.Version.DisplayName, categories);
        }
    }
}
