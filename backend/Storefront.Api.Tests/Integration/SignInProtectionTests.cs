using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Storefront.Api.Data.Entities;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Sign-in protection (Security-Audit.md, SA-03, SA-05, SA-06): per-client limits count an IPv6 /64 as
/// one client; failed sign-ins are throttled per account and client instead of locking the account
/// for everyone; and an unknown email costs the same password hash as a real one.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class SignInProtectionTests : IDisposable
{
    private const string Password = "Str0ng!Passw0rd";
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly CountingHasher _hasher = new();
    private readonly StorefrontApiFactory _factory;

    /// <summary>Counts password-hash verifications, the expensive step whose absence would leak an unknown email.</summary>
    private sealed class CountingHasher : PasswordHasher<StorefrontUser>
    {
        public int Verifications;

        public override PasswordVerificationResult VerifyHashedPassword(StorefrontUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref Verifications);
            return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    public SignInProtectionTests() => _factory = Create(accountPermitLimit: 1000);

    public void Dispose() => _factory.Dispose();

    private StorefrontApiFactory Create(int accountPermitLimit) => new(
        accountPermitLimit: accountPermitLimit,
        timeProvider: _clock,
        settings: new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:ClientAddressHeader"] = "CF-Connecting-IP",
        },
        configureServices: services =>
        {
            services.RemoveAll<IPasswordHasher<StorefrontUser>>();
            services.AddSingleton<IPasswordHasher<StorefrontUser>>(_hasher);
        });

    private static async Task<HttpResponseMessage> Login(StorefrontApiFactory factory, string from, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login") { Content = JsonContent.Create(new { email, password }) };
        request.Headers.Add("CF-Connecting-IP", from);
        return await factory.CreateClient().SendAsync(request);
    }

    private async Task<string> Account()
    {
        var email = $"protect-{Guid.NewGuid():N}@example.com";
        var register = await _factory.CreateClient().PostAsJsonAsync("/api/account/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        return email;
    }

    // ---------- SA-03: client partitioning ----------

    [Fact]
    public async Task Addresses_in_one_ipv6_64_share_one_rate_limit()
    {
        using var factory = Create(accountPermitLimit: 10);
        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 11; i++)
        {
            statuses.Add((await Login(factory, $"2001:db8:1:2::{i:x}", $"nobody{i}@example.com", "x")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task Different_ipv6_64s_and_ipv4_addresses_are_different_clients()
    {
        using var factory = Create(accountPermitLimit: 1);
        var a = await Login(factory, "2001:db8:1:2::1", "a@example.com", "x");
        var b = await Login(factory, "2001:db8:1:3::1", "b@example.com", "x");
        var c = await Login(factory, "198.51.100.1", "c@example.com", "x");
        var d = await Login(factory, "198.51.100.2", "d@example.com", "x");
        var again = await Login(factory, "2001:db8:1:2::ffff", "e@example.com", "x");

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized],
            new[] { a, b, c, d }.Select(r => r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode); // same /64 as the first
    }

    // ---------- SA-03 and SA-06: per-account sign-in protection ----------

    [Fact]
    public async Task Five_failures_from_one_client_block_that_client_for_that_account_even_with_the_right_password()
    {
        var email = await Account();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_factory, "198.51.100.7", email, "Wr0ng!Password")).StatusCode);
        }

        var blocked = await Login(_factory, "198.51.100.7", email, Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.InRange(blocked.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Equal("TOO_MANY_REQUESTS", (await AuthorizationTests.ReadJson(blocked)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_attacker_failing_from_one_network_cannot_lock_the_owner_out_from_another()
    {
        var email = await Account();
        for (var i = 0; i < 8; i++)
        {
            await Login(_factory, "198.51.100.66", email, "Wr0ng!Password");
        }

        var owner = await Login(_factory, "203.0.113.5", email, Password);

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task Twenty_failures_across_networks_block_the_account_until_the_window_ends()
    {
        var email = await Account();
        for (var i = 0; i < 20; i++)
        {
            await Login(_factory, $"198.51.100.{i + 10}", email, "Wr0ng!Password");
        }

        var duringWindow = await Login(_factory, "203.0.113.200", email, Password);
        _clock.Advance(TimeSpan.FromMinutes(15));
        var afterWindow = await Login(_factory, "203.0.113.200", email, Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, duringWindow.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterWindow.StatusCode);
    }

    [Fact]
    public async Task A_correct_password_resets_the_clients_count()
    {
        var email = await Account();
        for (var i = 0; i < 4; i++)
        {
            await Login(_factory, "198.51.100.8", email, "Wr0ng!Password");
        }

        Assert.Equal(HttpStatusCode.OK, (await Login(_factory, "198.51.100.8", email, Password)).StatusCode);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_factory, "198.51.100.8", email, "Wr0ng!Password")).StatusCode);
        }
    }

    [Fact]
    public async Task Repeated_failures_never_write_an_identity_lockout()
    {
        var email = await Account();
        for (var i = 0; i < 6; i++)
        {
            await Login(_factory, "198.51.100.9", email, "Wr0ng!Password");
        }

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StorefrontUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.Null(user!.LockoutEnd);
        Assert.Equal(0, user.AccessFailedCount);
    }

    // ---------- SA-05: no account enumeration ----------

    [Fact]
    public async Task Unknown_and_known_emails_get_the_same_answers_and_the_same_hashing_work()
    {
        var email = await Account();
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";

        var before = _hasher.Verifications;
        var wrongPassword = await Login(_factory, "198.51.100.30", email, "Wr0ng!Password");
        var known = _hasher.Verifications - before;
        before = _hasher.Verifications;
        var noAccount = await Login(_factory, "198.51.100.30", unknown, "Wr0ng!Password");
        var unknownCost = _hasher.Verifications - before;

        Assert.Equal(1, known);
        Assert.Equal(1, unknownCost);
        Assert.Equal(wrongPassword.StatusCode, noAccount.StatusCode);
        Assert.Equal(
            (await AuthorizationTests.ReadJson(wrongPassword)).GetProperty("detail").GetString(),
            (await AuthorizationTests.ReadJson(noAccount)).GetProperty("detail").GetString());

        // Throttling behaves the same for an email with no account: 429 after five, like a real one.
        for (var i = 0; i < 4; i++)
        {
            await Login(_factory, "198.51.100.31", unknown, "Wr0ng!Password");
            await Login(_factory, "198.51.100.31", email, "Wr0ng!Password");
        }

        await Login(_factory, "198.51.100.31", unknown, "Wr0ng!Password");
        await Login(_factory, "198.51.100.31", email, "Wr0ng!Password");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(_factory, "198.51.100.31", unknown, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(_factory, "198.51.100.31", email, "x")).StatusCode);
    }

    [Fact]
    public async Task A_blocked_attempt_is_answered_without_checking_the_password()
    {
        var email = await Account();
        for (var i = 0; i < 5; i++)
        {
            await Login(_factory, "198.51.100.40", email, "Wr0ng!Password");
        }

        var before = _hasher.Verifications;
        var blocked = await Login(_factory, "198.51.100.40", email, Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(before, _hasher.Verifications);
    }
}
