using FluentAssertions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Access;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class AccessTokenTests
{
    [SqlServerFact]
    public async Task Two_redemptions_one_success()
    {
        await using var db = await Fixture.CreateAsync();
        await db.Access.RequestAsync(new AccessLinkRequest(1, null, "person@example.test"));
        db.Email.Tokens.Should().ContainSingle().Which.Length.Should().BeGreaterThan(30);
        var token = db.Email.Tokens.Single();
        var normalized = token.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        var decoded = Convert.FromBase64String(normalized);
        decoded.Length.Should().Be(32);
        await using (var context = db.Context())
        {
            var digest = (await context.ResponseAccessTokens.SingleAsync()).TokenDigest;
            digest.Should().HaveCount(32);
            digest.Should().Equal(SHA256.HashData(decoded));
            Convert.ToBase64String(digest).Should().NotBe(token);
        }
        var results = await Task.WhenAll(AttemptAsync(db, token), AttemptAsync(db, token));
        results.Count(grant => grant is not null).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Expired_link_and_wrong_response_fail()
    {
        await using var db = await Fixture.CreateAsync();
        await db.Access.RequestAsync(new AccessLinkRequest(1, null, "person@example.test"));
        var token = db.Email.Tokens.Single();
        db.Clock.Advance(TimeSpan.FromMinutes(31));
        Func<Task> expired = () => db.Access.RedeemAsync(token);
        await expired.Should().ThrowAsync<InvalidAccessLinkException>();
        db.Clock.Reset();
        // A resume request for another owner's response is indistinguishable from an unknown address.
        await db.Access.RequestAsync(new AccessLinkRequest(1, Guid.NewGuid(), "person@example.test"));
        db.Email.Tokens.Should().ContainSingle();
    }

    [SqlServerFact]
    public async Task Request_for_unknown_email_has_same_public_response()
    {
        await using var db = await Fixture.CreateAsync();
        await db.Access.RequestAsync(new AccessLinkRequest(1, Guid.NewGuid(), "person@example.test"));
        var sent = db.Email.Tokens.Count;
        await db.Access.RequestAsync(new AccessLinkRequest(1, Guid.NewGuid(), "other@example.test"));
        db.Email.Tokens.Should().HaveCount(sent);
    }

    [SqlServerFact]
    public async Task New_start_link_after_stop_does_not_create_response()
    {
        await using var db = await Fixture.CreateAsync();
        await db.CloseAsync();
        await db.Access.RequestAsync(new AccessLinkRequest(1, null, "person@example.test"));
        db.Email.Tokens.Should().BeEmpty();
        await using var context = db.Context();
        (await context.ResponseDrafts.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task Existing_resume_link_after_stop_still_works()
    {
        await using var db = await Fixture.CreateAsync();
        await db.Access.RequestAsync(new AccessLinkRequest(1, null, "person@example.test"));
        var startGrant = await db.Access.RedeemAsync(db.Email.Tokens.Single());
        var respondent = new TestRespondent(new RespondentIdentity(startGrant.OwnerKey, null, startGrant.ApplicationVersionId, startGrant.AccessTokenId));
        var started = await new ResponseService(new Factory(db.Sql), respondent).StartAsync(1);
        await db.Access.RequestAsync(new AccessLinkRequest(1, started.Id, "person@example.test"));
        var resumeToken = db.Email.Tokens.Last();
        await db.CloseAsync();
        var resumeGrant = await db.Access.RedeemAsync(resumeToken);
        var resumed = await new ResponseService(new Factory(db.Sql), new TestRespondent(new RespondentIdentity(resumeGrant.OwnerKey,
            resumeGrant.ResponseId, resumeGrant.ApplicationVersionId, resumeGrant.AccessTokenId))).GetAsync(started.Id);
        resumed.Id.Should().Be(started.Id);
    }

    [SqlServerFact]
    public async Task Redeemed_start_grant_maps_to_one_response()
    {
        await using var db = await Fixture.CreateAsync();
        await db.Access.RequestAsync(new AccessLinkRequest(1, null, "person@example.test"));
        var grant = await db.Access.RedeemAsync(db.Email.Tokens.Single());
        var identity = new RespondentIdentity(grant.OwnerKey, null, grant.ApplicationVersionId, grant.AccessTokenId);
        var service = new ResponseService(new Factory(db.Sql), new TestRespondent(identity));
        var first = await service.StartAsync(1);
        var second = await service.StartAsync(1);
        first.Id.Should().Be(second.Id);
        await using var context = db.Context();
        (await context.ResponseDrafts.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Email_link_issuance_is_limited_per_address_and_application()
    {
        await using var db = await Fixture.CreateAsync();
        for (var i = 0; i < 6; i++) await db.Access.RequestAsync(new AccessLinkRequest(1, null, "person@example.test"));
        db.Email.Tokens.Should().HaveCount(5);
    }

    private static async Task<AccessGrant?> AttemptAsync(Fixture db, string token)
    { try { return await db.Access.RedeemAsync(token); } catch (InvalidAccessLinkException) { return null; } }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqlServerDatabase sql, EmailSender email, ManualClock clock) { Sql = sql; Email = email; Clock = clock; }
        public SqlServerDatabase Sql { get; }
        public EmailSender Email { get; }
        public ManualClock Clock { get; }
        public IResponseAccessService Access => new ResponseAccessService(new Factory(Sql), Email, new LinkBuilder(), Clock,
            Microsoft.Extensions.Options.Options.Create(new ResponseAccessOptions()));
        public QuestionAppBuilderDbContext Context() => Sql.CreateContext();
        public static async Task<Fixture> CreateAsync()
        {
            var db = new Fixture(await SqlServerDatabase.CreateAsync(), new EmailSender(), new ManualClock());
            await using var context = db.Context();
            await context.Database.MigrateAsync();
            var app = new QuestionAppBuilder.Domain.Entities.Application { Name = "Email access", ApplicationTypeId = 1,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, AcceptNewResponses = true, AcceptSubmissions = true,
                Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow,
                    PublishedUtc = DateTime.UtcNow, DisplayName = "v1", RespondentAccessMode = RespondentAccessMode.EmailLink,
                    Categories = { new Category { Name = "Basics", SortOrder = 1, Elements = { new Element { Kind = ElementKind.Question,
                        Label = "Optional", SortOrder = 1, Question = new Question { AnswerType = AnswerType.Textbox } } } } } } } };
            context.Applications.Add(app); await context.SaveChangesAsync(); app.CurrentVersionId = app.Versions.Single().Id; await context.SaveChangesAsync();
            return db;
        }
        public async Task CloseAsync()
        {
            await using var context = Context(); var app = await context.Applications.SingleAsync();
            await new AvailabilityService(new Factory(Sql), new TestActor()).SetAsync(new AvailabilityUpdate(app.Id, app.RowVersion.ToArray(), false, false));
        }
        public ValueTask DisposeAsync() => Sql.DisposeAsync();
    }

    private sealed class EmailSender : IResponseEmailSender
    {
        public List<string> Tokens { get; } = new();
        public Task SendLinkAsync(string email, Uri link, CancellationToken cancellationToken = default)
        { Tokens.Add(System.Web.HttpUtility.ParseQueryString(link.Query)["token"]!); return Task.CompletedTask; }
    }
    private sealed class LinkBuilder : IResponseAccessLinkBuilder
    { public Uri CreateLink(string token) => new($"https://trusted.example.test/confirm?token={Uri.EscapeDataString(token)}"); }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
        public void Reset() => _now = DateTimeOffset.UtcNow;
    }
    private sealed class TestRespondent(RespondentIdentity identity) : IRespondentContext { public RespondentIdentity GetRequiredIdentity() => identity; }
    private sealed class TestActor : ITrustedActorContext { public string GetRequiredActorKey() => "admin"; }
    private sealed class Factory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(database.CreateContext());
    }
}
