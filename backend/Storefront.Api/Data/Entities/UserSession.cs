namespace Storefront.Api.Data.Entities;

/// <summary>
/// One sign-in: the server-side record behind a pair of bearer and refresh tokens (Security-Audit.md, SA-02).
///
/// Both tokens carry this session's ID, so revoking the row ends the session at once: the access token
/// stops working on its next request, and the refresh token can't be exchanged. The refresh token also
/// carries a one-time ID; each refresh replaces it, and presenting a replaced one revokes the session.
/// </summary>
public sealed class UserSession
{
    /// <summary>Random, 32 hex characters.</summary>
    public required string Id { get; set; }

    public required string UserId { get; set; }

    public StorefrontUser? User { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Absolute end of the session: refreshing never extends it.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? LastRefreshedAtUtc { get; set; }

    /// <summary>The one refresh token (by its ID) that may be exchanged next.</summary>
    public required string CurrentTokenId { get; set; }

    /// <summary>The token replaced by the last refresh, accepted until <see cref="PreviousTokenValidUntilUtc"/> (a retry after a lost response).</summary>
    public string? PreviousTokenId { get; set; }

    public DateTime? PreviousTokenValidUntilUtc { get; set; }

    /// <summary>The user's security stamp at sign-in. A password change replaces the stamp, which ends the session.</summary>
    public required string SecurityStamp { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>signed-out, refresh-token-reused, credentials-changed or replaced.</summary>
    public string? RevokedReason { get; set; }
}
