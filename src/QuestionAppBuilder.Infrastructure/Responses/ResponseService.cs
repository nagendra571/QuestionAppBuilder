using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Responses;

public sealed class ResponseService : IResponseService
{
    private const decimal MaximumDecimal18Scale4 = 99_999_999_999_999.9999m;
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;
    private readonly IRespondentContext _respondentContext;

    public ResponseService(IDbContextFactory<QuestionAppBuilderDbContext> factory, IRespondentContext respondentContext)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _respondentContext = respondentContext ?? throw new ArgumentNullException(nameof(respondentContext));
    }

    public async Task<ResponseSnapshot> StartAsync(int applicationId, CancellationToken ct = default)
    {
        var identity = RequiredIdentity();
        if (identity.GrantedResponseId is Guid grantedResponseId)
        {
            var granted = await GetAsync(grantedResponseId, ct);
            if (granted.ApplicationId != applicationId) throw new NotFoundException("The response was not found.");
            return granted;
        }
        await using var db = await _factory.CreateDbContextAsync(ct);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, ct)
            ?? throw new NotFoundException($"Application {applicationId} was not found.");
        if (!application.AcceptNewResponses || application.CurrentVersionId is not int currentVersionId)
            throw new NotFoundException($"Application {applicationId} is not accepting new responses.");
        if (identity.GrantedVersionId is int grantedVersion && grantedVersion != currentVersionId)
            throw new NotFoundException("The response start was not found.");
        var version = await db.ApplicationVersions.SingleOrDefaultAsync(v => v.Id == currentVersionId
            && v.ApplicationId == application.Id && v.Status == VersionStatus.Published, ct)
            ?? throw new NotFoundException("The published definition was not found.");
        if (string.IsNullOrWhiteSpace(identity.OwnerKey) || identity.OwnerKey.Length > 256)
            throw new InvalidOperationException("Respondent identity is not configured correctly.");
        var now = DateTime.UtcNow;
        var response = new ResponseDraft
        {
            Id = Guid.NewGuid(), ApplicationId = application.Id, ApplicationVersionId = version.Id,
            OwnerKey = identity.OwnerKey, Status = ResponseStatus.InProgress, CreatedUtc = now, UpdatedUtc = now
        };
        db.ResponseDrafts.Add(response);
        await db.SaveChangesAsync(ct);
        var graph = await LoadGraphAsync(db, version.Id, ct);
        return Snapshot(response, graph, new Dictionary<int, AnswerValue>(), application);
    }

    public async Task<ResponseSnapshot> GetAsync(Guid responseId, CancellationToken ct = default)
    {
        var identity = RequiredIdentity();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var response = await db.ResponseDrafts.AsNoTracking()
            .Include(r => r.Answers).ThenInclude(a => a.SelectedOptions)
            .SingleOrDefaultAsync(r => r.Id == responseId, ct);
        if (response is null || !CanAccess(identity, response)) throw new NotFoundException("The response was not found.");
        var application = await db.Applications.AsNoTracking().SingleAsync(a => a.Id == response.ApplicationId, ct);
        var graph = await LoadGraphAsync(db, response.ApplicationVersionId, ct);
        return Snapshot(response, graph, ToValues(response.Answers), application);
    }

    public async Task<ResponseSnapshot> SaveAsync(SaveResponseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedRevision);
        ArgumentNullException.ThrowIfNull(request.Answers);
        var identity = RequiredIdentity();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var response = await db.ResponseDrafts.Include(r => r.Answers).ThenInclude(a => a.SelectedOptions)
            .SingleOrDefaultAsync(r => r.Id == request.ResponseId, ct);
        if (response is null || !CanAccess(identity, response)) throw new NotFoundException("The response was not found.");
        if (response.Status != ResponseStatus.InProgress) throw new ResponseConflictException();
        if (!response.RevisionNonce.ToByteArray().AsSpan().SequenceEqual(request.ExpectedRevision)) throw new ResponseConflictException();
        var graph = await LoadGraphAsync(db, response.ApplicationVersionId, ct);
        ValidateStoredValues(graph, request.Answers);

        response.Answers.Clear();
        foreach (var (questionId, value) in request.Answers)
        {
            if (!HasValue(value)) continue;
            var answer = new ResponseAnswer
            {
                Id = Guid.NewGuid(), ResponseDraftId = response.Id, QuestionId = questionId,
                TextValue = value.Text, NumberValue = value.Number
            };
            foreach (var optionId in value.SelectedOptionIds ?? Array.Empty<int>())
                answer.SelectedOptions.Add(new ResponseSelectedOption { QuestionOptionId = optionId });
            response.Answers.Add(answer);
            db.Entry(answer).State = EntityState.Added;
        }
        response.RevisionNonce = Guid.NewGuid();
        response.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException exception) { throw new ResponseConflictException(exception); }
        if (transaction is not null) await transaction.CommitAsync(ct);
        var application = await db.Applications.AsNoTracking().SingleAsync(a => a.Id == response.ApplicationId, ct);
        return Snapshot(response, graph, ToValues(response.Answers), application);
    }

    public async Task<IReadOnlyList<ResponseSummary>> ListMineAsync(int take, DateTime? beforeUtc = null, Guid? beforeId = null, CancellationToken ct = default)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take), "Page size must be between 1 and 100.");
        var identity = RequiredIdentity();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.ResponseDrafts.AsNoTracking().Where(r => r.OwnerKey == identity.OwnerKey);
        if (beforeUtc is DateTime cursorUtc && beforeId is Guid cursorId)
            query = query.Where(r => r.UpdatedUtc < cursorUtc || r.UpdatedUtc == cursorUtc && r.Id.CompareTo(cursorId) < 0);
        else if (beforeUtc is not null || beforeId is not null)
            throw new ArgumentException("Both cursor values are required.");
        return await (from response in query
            join version in db.ApplicationVersions.AsNoTracking() on response.ApplicationVersionId equals version.Id
            orderby response.UpdatedUtc descending, response.Id descending
            select new ResponseSummary(response.Id, response.ApplicationId, response.ApplicationVersionId, version.DisplayName, response.UpdatedUtc))
            .Take(take).ToListAsync(ct);
    }

    private RespondentIdentity RequiredIdentity()
    {
        var identity = _respondentContext.GetRequiredIdentity();
        if (identity is null || string.IsNullOrWhiteSpace(identity.OwnerKey)) throw new InvalidOperationException("A respondent identity is required.");
        return identity;
    }

    private static bool CanAccess(RespondentIdentity identity, ResponseDraft response)
        => StringComparer.Ordinal.Equals(identity.OwnerKey, response.OwnerKey)
            || identity.GrantedResponseId == response.Id && (identity.GrantedVersionId is null || identity.GrantedVersionId == response.ApplicationVersionId);

    private static async Task<ApplicationVersionGraph> LoadGraphAsync(QuestionAppBuilderDbContext db, int versionId, CancellationToken ct)
    {
        var version = await db.ApplicationVersions.AsNoTracking()
            .Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Options)
            .Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Rules).ThenInclude(r => r.Options)
            .Include(v => v.VisibilityConditions).ThenInclude(c => c.Predicates).ThenInclude(p => p.Options)
            .SingleOrDefaultAsync(v => v.Id == versionId && v.Status == VersionStatus.Published, ct)
            ?? throw new NotFoundException("The pinned questionnaire definition was not found.");
        return new ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions };
    }

    private static ResponseSnapshot Snapshot(ResponseDraft response, ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers,
        QuestionAppBuilder.Domain.Entities.Application application)
        => new(response.Id, response.ApplicationId, response.ApplicationVersionId, graph, answers, response.Status,
            response.RevisionNonce.ToByteArray(), response.UpdatedUtc, application.AcceptNewResponses, application.AcceptSubmissions);

    private static Dictionary<int, AnswerValue> ToValues(IEnumerable<ResponseAnswer> answers)
        => answers.ToDictionary(answer => answer.QuestionId, answer => new AnswerValue(answer.TextValue, answer.NumberValue,
            answer.SelectedOptions.Select(option => option.QuestionOptionId).ToArray()));

    private static bool HasValue(AnswerValue value) => !string.IsNullOrWhiteSpace(value.Text) || value.Number is not null || value.SelectedOptionIds is { Count: > 0 };

    private static void ValidateStoredValues(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers)
    {
        var questions = graph.Categories.SelectMany(c => c.Elements).Where(e => e.Question is not null).Select(e => e.Question!).ToDictionary(q => q.Id);
        var invalid = new HashSet<int>();
        foreach (var (questionId, answer) in answers)
        {
            if (!questions.TryGetValue(questionId, out var question) || answer is null)
            { invalid.Add(questionId); continue; }
            var selected = answer.SelectedOptionIds ?? Array.Empty<int>();
            var allowed = question.Options.Select(option => option.Id).ToHashSet();
            var bad = question.AnswerType switch
            {
                AnswerType.Textbox or AnswerType.Textarea => answer.Number is not null || selected.Count != 0
                    || answer.Text?.Length > ValidationLimits.AnswerTextMaxLength || answer.Text is not null && question.MaxLength is int maxText && answer.Text.Length > maxText,
                AnswerType.Number => answer.Text is not null || selected.Count != 0 || answer.Number is decimal number
                    && (number < MaximumDecimal18Scale4 * -1 || number > MaximumDecimal18Scale4 || decimal.Round(number, 4) != number
                        || question.MinValue is decimal min && number < min || question.MaxValue is decimal max && number > max),
                AnswerType.Dropdown or AnswerType.RadioGroup => answer.Text is not null || answer.Number is not null || selected.Count > 1
                    || selected.Distinct().Count() != selected.Count || selected.Any(id => !allowed.Contains(id)),
                AnswerType.Checkboxes => answer.Text is not null || answer.Number is not null || selected.Distinct().Count() != selected.Count
                    || selected.Any(id => !allowed.Contains(id)) || question.MaxSelections is int max && selected.Count > max,
                _ => true
            };
            if (bad) invalid.Add(questionId);
        }
        if (invalid.Count != 0) throw new AnswerValidationException("One or more answers are malformed, too long, outside the supported numeric precision, or reference an invalid question or option.", invalid.Order().ToArray());
    }
}
