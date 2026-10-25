using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Security;

/// <summary>Settings (section "Sessions").</summary>
public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>
    /// How long the refresh token replaced by a refresh is still accepted, so a client whose response
    /// was lost (dropped connection, page reload mid-request) can retry instead of being signed out.
    /// Presenting it later counts as reuse and ends the session.
    /// </summary>
    [Range(0, 300)]
    public int RefreshReuseGraceSeconds { get; set; } = 30;

    /// <summary>Active sessions kept per account; signing in beyond this ends the oldest.</summary>
    [Range(1, 100)]
    public int MaxActiveSessionsPerUser { get; set; } = 10;
}

/// <summary>Claims added to both tokens of a session.</summary>
public static class SessionClaims
{
    public const string SessionId = "storefront.sid";
    public const string RefreshTokenId = "storefront.rtid";

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static ClaimsPrincipal WithSession(ClaimsPrincipal principal, string sessionId, string refreshTokenId)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(SessionId, sessionId));
        identity.AddClaim(new Claim(RefreshTokenId, refreshTokenId));
        return principal;
    }
}

public enum RefreshRejection
{
    None,
    Unknown,
    Ended,
    CredentialsChanged,
    Reused,
}

/// <summary>
/// Server-side sign-in sessions (Security-Audit.md, SA-02). Every token belongs to a row in
/// UserSessions, so a session can be ended for real: by signing out, by reuse of a replaced refresh
/// token, by a password change (the account's security stamp no longer matches), or when it expires.
/// </summary>
public sealed class SessionService(
    StorefrontDbContext db,
    TimeProvider timeProvider,
    IOptions<SessionOptions> options,
    IOptionsMonitor<BearerTokenOptions> bearerOptions,
    ILogger<SessionService> logger)
{
    private readonly SessionOptions _options = options.Value;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<UserSession> StartAsync(StorefrontUser user, CancellationToken cancellationToken)
    {
        var now = Now;

        // Housekeeping for this account: forget sessions that ended more than a day ago, and keep at
        // most MaxActiveSessionsPerUser active ones (the new one included).
        var cutoff = now.AddDays(-1);
        await db.UserSessions
            .Where(s => s.UserId == user.Id && (s.ExpiresAtUtc < cutoff || s.RevokedAtUtc < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
        var surplus = await db.UserSessions
            .Where(s => s.UserId == user.Id && s.RevokedAtUtc == null && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Skip(_options.MaxActiveSessionsPerUser - 1)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (surplus.Count > 0)
        {
            await db.UserSessions
                .Where(s => surplus.Contains(s.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAtUtc, now).SetProperty(s => s.RevokedReason, "replaced"), cancellationToken);
        }

        var session = new UserSession
        {
            Id = SessionClaims.NewId(),
            UserId = user.Id,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + bearerOptions.Get(Microsoft.AspNetCore.Identity.IdentityConstants.BearerScheme).RefreshTokenExpiration,
            CurrentTokenId = SessionClaims.NewId(),
            SecurityStamp = user.SecurityStamp ?? string.Empty,
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(session).State = EntityState.Detached;
        return session;
    }

    /// <summary>
    /// Exchanges the refresh token <paramref name="tokenId"/> of session <paramref name="sessionId"/> for
    /// the ID the next refresh token must carry. Each token can be exchanged once; the one just replaced
    /// is honoured for a short grace period, and any other old token revokes the whole session.
    /// </summary>
    public async Task<(string? NextTokenId, StorefrontUser? User, RefreshRejection Rejection)> RotateAsync(
        string sessionId, string tokenId, CancellationToken cancellationToken)
    {
        var now = Now;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await db.UserSessions.AsNoTracking().Include(s => s.User)
                .SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session?.User is null)
            {
                return (null, null, RefreshRejection.Unknown);
            }

            if (session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now)
            {
                return (null, null, RefreshRejection.Ended);
            }

            if (session.SecurityStamp != (session.User.SecurityStamp ?? string.Empty))
            {
                await RevokeAsync(sessionId, "credentials-changed", cancellationToken);
                return (null, null, RefreshRejection.CredentialsChanged);
            }

            if (tokenId == session.CurrentTokenId)
            {
                var next = SessionClaims.NewId();
                var updated = await db.UserSessions
                    .Where(s => s.Id == sessionId && s.CurrentTokenId == tokenId && s.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.PreviousTokenId, tokenId)
                        .SetProperty(s => s.PreviousTokenValidUntilUtc, now.AddSeconds(_options.RefreshReuseGraceSeconds))
                        .SetProperty(s => s.CurrentTokenId, next)
                        .SetProperty(s => s.LastRefreshedAtUtc, now), cancellationToken);
                if (updated == 1)
                {
                    return (next, session.User, RefreshRejection.None);
                }

                continue; // a concurrent refresh with the same token got there first: look again
            }

            if (tokenId == session.PreviousTokenId && session.PreviousTokenValidUntilUtc > now)
            {
                // A retry of a refresh whose response was lost: hand out the current token again.
                return (session.CurrentTokenId, session.User, RefreshRejection.None);
            }

            break;
        }

        await RevokeAsync(sessionId, "refresh-token-reused", cancellationToken);
        logger.LogWarning("Session {SessionId} ended: an already-used refresh token was presented again.", sessionId);
        return (null, null, RefreshRejection.Reused);
    }

    public Task<int> RevokeAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        var now = Now;
        return db.UserSessions
            .Where(s => s.Id == sessionId && s.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAtUtc, now).SetProperty(s => s.RevokedReason, reason), cancellationToken);
    }

    /// <summary>The session exists, belongs to the user, hasn't ended, and the account's credentials haven't changed since.</summary>
    public Task<bool> IsActiveAsync(string sessionId, string userId, CancellationToken cancellationToken)
    {
        var now = Now;
        return db.UserSessions.AsNoTracking().AnyAsync(
            s => s.Id == sessionId
                 && s.UserId == userId
                 && s.RevokedAtUtc == null
                 && s.ExpiresAtUtc > now
                 && s.SecurityStamp == (s.User!.SecurityStamp ?? string.Empty),
            cancellationToken);
    }
}

/// <summary>
/// Checks every bearer token against its session before the request is authenticated. A token whose
/// session has ended (or which predates server-side sessions) fails authentication: the caller gets
/// 401, as for an expired token, and the frontend refreshes or signs in again.
/// </summary>
public static class SessionValidation
{
    public static async Task OnMessageReceivedAsync(MessageReceivedContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return;
        }

        var ticket = context.Options.BearerTokenProtector.Unprotect(header["Bearer ".Length..].Trim());
        if (ticket is null)
        {
            return; // not a token at all: the handler itself rejects it
        }

        var sessionId = ticket.Principal.FindFirstValue(SessionClaims.SessionId);
        var userId = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sessionId is null || userId is null)
        {
            context.Fail("The token has no session.");
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
        if (!await sessions.IsActiveAsync(sessionId, userId, context.HttpContext.RequestAborted))
        {
            context.Fail("The session has ended.");
        }
    }
}
