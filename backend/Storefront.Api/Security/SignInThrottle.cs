using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Storefront.Api.Security;

/// <summary>Settings (section "SignInProtection"). Counts failed sign-ins in fixed windows.</summary>
public sealed class SignInProtectionOptions
{
    public const string SectionName = "SignInProtection";

    /// <summary>Failures allowed for one account from one client (IPv4 address or IPv6 /64) per window.</summary>
    [Range(1, 1000)]
    public int FailuresPerAccountAndClient { get; set; } = 5;

    /// <summary>Failures allowed for one account from all clients together per window.</summary>
    [Range(1, 10_000)]
    public int FailuresPerAccount { get; set; } = 20;

    [Range(1, 1440)]
    public int WindowMinutes { get; set; } = 15;
}

/// <summary>
/// Per-account sign-in protection (Security-Audit.md, SA-03 and SA-06). It replaces Identity's lockout,
/// which locked the account for everyone after 5 failures, so anyone who knew an email could keep its
/// owner out.
///
/// Failed attempts are counted per account, by a hash of the normalised email, and per account plus
/// client network:
///  - 5 failures for one account from one client: that client gets 429 for that account, for the rest
///    of the window. The owner signing in from elsewhere is unaffected.
///  - 20 failures for one account from all clients together: every client gets 429 for that account
///    until the window ends. This stops guessing spread over many networks, at the cost that a
///    distributed attacker can still block one account for a while.
///
/// A blocked attempt is answered before the password is checked. Counting works the same for emails
/// with no account, so neither the counts nor a 429 reveal whether an account exists.
///
/// The counters live in memory: they reset when the process restarts, and aren't shared between
/// instances. With one instance (as deployed) that's sufficient; several instances would need a shared store.
/// </summary>
public sealed class SignInThrottle(IOptions<SignInProtectionOptions> options, TimeProvider timeProvider)
{
    private const int PruneAbove = 50_000;

    private readonly SignInProtectionOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Failures;
    }

    private TimeSpan Length => TimeSpan.FromMinutes(_options.WindowMinutes);

    /// <summary>A stable, non-reversible key for an email (the address itself is never kept or logged).</summary>
    public static string AccountKey(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant())))[..32];

    /// <summary>How long this client must wait before trying this account again, or null when it may try now.</summary>
    public TimeSpan? RetryAfter(string accountKey, string client)
    {
        var now = timeProvider.GetUtcNow();
        var wait = TimeSpan.Zero;
        if (Current(PairKey(accountKey, client), now) is { } pair && pair.Failures >= _options.FailuresPerAccountAndClient)
        {
            wait = Max(wait, pair.Start + Length - now);
        }

        if (Current(AccountOnly(accountKey), now) is { } account && account.Failures >= _options.FailuresPerAccount)
        {
            wait = Max(wait, account.Start + Length - now);
        }

        return wait > TimeSpan.Zero ? wait : null;
    }

    public void RecordFailure(string accountKey, string client)
    {
        var now = timeProvider.GetUtcNow();
        Increment(PairKey(accountKey, client), now);
        Increment(AccountOnly(accountKey), now);
        if (_windows.Count > PruneAbove)
        {
            Prune(now);
        }
    }

    /// <summary>A correct password clears this client's count for the account (not the account-wide one).</summary>
    public void RecordSuccess(string accountKey, string client) => _windows.TryRemove(PairKey(accountKey, client), out _);

    private static string PairKey(string account, string client) => $"p:{account}:{client}";

    private static string AccountOnly(string account) => $"a:{account}";

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private Window? Current(string key, DateTimeOffset now) =>
        _windows.TryGetValue(key, out var window) && now - window.Start < Length ? window : null;

    private void Increment(string key, DateTimeOffset now)
    {
        var window = _windows.GetOrAdd(key, _ => new Window(now));
        lock (window)
        {
            if (now - window.Start >= Length)
            {
                window.Start = now;
                window.Failures = 0;
            }

            window.Failures++;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
        {
            if (now - window.Start >= Length)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }
}
