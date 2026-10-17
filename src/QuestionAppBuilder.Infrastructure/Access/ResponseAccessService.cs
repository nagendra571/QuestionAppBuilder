using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Access;
using Microsoft.Extensions.Options;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Access;

public sealed class ResponseAccessService : IResponseAccessService
{
    private readonly IDbContextFactory<QuestionAppBuilderDbContext> _factory;
    private readonly IResponseEmailSender _emailSender;
    private readonly IResponseAccessLinkBuilder _linkBuilder;
    private readonly TimeProvider _time;
    private readonly ResponseAccessOptions _options;

    public ResponseAccessService(IDbContextFactory<QuestionAppBuilderDbContext> factory, IResponseEmailSender emailSender,
        IResponseAccessLinkBuilder linkBuilder, TimeProvider time, IOptions<ResponseAccessOptions> options)
    { _factory = factory; _emailSender = emailSender; _linkBuilder = linkBuilder; _time = time; _options = options.Value; }

    public async Task RequestAsync(AccessLinkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = NormalizeEmail(request.Email);
        if (email is null) return;
        var ownerKey = OwnerKey(email);
        var now = _time.GetUtcNow().UtcDateTime;
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64Url(tokenBytes);
        Uri? link = null;
        var shouldSend = false;
        await using (var strategyDb = await _factory.CreateDbContextAsync(cancellationToken))
        {
            var strategy = strategyDb.Database.CreateExecutionStrategy();
            shouldSend = await strategy.ExecuteAsync(async () =>
            {
                await using var db = await _factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await SqlApplicationLock.AcquireAsync(db, request.ApplicationId, cancellationToken);
                var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == request.ApplicationId, cancellationToken);
                if (application?.CurrentVersionId is not int currentVersionId) { await transaction.CommitAsync(cancellationToken); return false; }
                ApplicationVersion version;
                if (request.ResponseId is Guid responseId)
                {
                    var response = await db.ResponseDrafts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == responseId && r.ApplicationId == request.ApplicationId, cancellationToken);
                    if (response is null || !StringComparer.Ordinal.Equals(response.OwnerKey, ownerKey)) { await transaction.CommitAsync(cancellationToken); return false; }
                    version = await db.ApplicationVersions.SingleAsync(v => v.Id == response.ApplicationVersionId, cancellationToken);
                }
                else
                {
                    if (!application.AcceptNewResponses || currentVersionId <= 0) { await transaction.CommitAsync(cancellationToken); return false; }
                    version = await db.ApplicationVersions.SingleAsync(v => v.Id == currentVersionId, cancellationToken);
                }
                if (version.Status != VersionStatus.Published || version.RespondentAccessMode != RespondentAccessMode.EmailLink)
                { await transaction.CommitAsync(cancellationToken); return false; }
                if (await db.ResponseAccessTokens.CountAsync(t => t.ApplicationId == request.ApplicationId && t.VerifiedEmail == email
                    && t.CreatedUtc >= now - _options.RateLimitWindow, cancellationToken) >= _options.MaxRequestsPerWindow)
                { await transaction.CommitAsync(cancellationToken); return false; }
                link = _linkBuilder.CreateLink(token);
                db.ResponseAccessTokens.Add(new ResponseAccessToken { Id = Guid.NewGuid(), TokenDigest = SHA256.HashData(tokenBytes),
                    ApplicationId = request.ApplicationId, ApplicationVersionId = version.Id, ResponseId = request.ResponseId,
                    OwnerKey = ownerKey, VerifiedEmail = email, CreatedUtc = now, ExpiresUtc = now + _options.LinkLifetime });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            });
        }
        if (shouldSend) await _emailSender.SendLinkAsync(email, link!, cancellationToken);
        CryptographicOperations.ZeroMemory(tokenBytes);
    }

    public async Task<AccessGrant> RedeemAsync(string token, CancellationToken cancellationToken = default)
    {
        var raw = TryDecode(token);
        if (raw is null) throw new InvalidAccessLinkException();
        try
        {
            var digest = SHA256.HashData(raw);
            var now = _time.GetUtcNow().UtcDateTime;
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var stored = await db.ResponseAccessTokens.SingleOrDefaultAsync(t => t.TokenDigest == digest, cancellationToken);
            if (stored is null || stored.ConsumedUtc is not null || stored.ExpiresUtc <= now)
                throw new InvalidAccessLinkException();
            var changed = await db.ResponseAccessTokens.Where(t => t.Id == stored.Id && t.ConsumedUtc == null && t.ExpiresUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(t => t.ConsumedUtc, now), cancellationToken);
            if (changed != 1) throw new InvalidAccessLinkException();
            await transaction.CommitAsync(cancellationToken);
            return new AccessGrant(stored.Id, stored.OwnerKey, stored.ApplicationId, stored.ApplicationVersionId,
                stored.ResponseId, now + _options.GrantLifetime);
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    internal static string OwnerKey(string normalizedEmail) => "email:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail))).ToLowerInvariant();

    private static string? NormalizeEmail(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        try
        {
            var parsed = new MailAddress(input.Trim());
            if (!StringComparer.OrdinalIgnoreCase.Equals(parsed.Address, input.Trim())) return null;
            return parsed.Address.ToLowerInvariant();
        }
        catch (FormatException) { return null; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[]? TryDecode(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
}
