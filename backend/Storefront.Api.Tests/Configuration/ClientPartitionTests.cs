using System.Net;
using Microsoft.Extensions.Options;
using Storefront.Api.Security;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Configuration;

/// <summary>Who counts as one client (Security-Audit.md, SA-03), and the sign-in throttle's arithmetic.</summary>
[Trait("Category", TestCategories.Unit)]
public sealed class ClientPartitionTests
{
    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    [InlineData("::1", "::/64")]
    public void Addresses_map_to_their_partition(string address, string partition) =>
        Assert.Equal(partition, ClientPartition.For(IPAddress.Parse(address)));

    [Fact]
    public void A_missing_address_is_its_own_partition() => Assert.Equal("unknown", ClientPartition.For((IPAddress?)null));

    private static (SignInThrottle Throttle, ManualTimeProvider Clock) Throttle(int perPair = 5, int perAccount = 20, int window = 15)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new SignInProtectionOptions { FailuresPerAccountAndClient = perPair, FailuresPerAccount = perAccount, WindowMinutes = window });
        return (new SignInThrottle(options, clock), clock);
    }

    [Fact]
    public void The_account_key_is_a_case_insensitive_hash_not_the_email()
    {
        var key = SignInThrottle.AccountKey(" Ada@Example.com ");
        Assert.Equal(key, SignInThrottle.AccountKey("ada@example.com"));
        Assert.DoesNotContain("ada", key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void The_pair_limit_blocks_only_that_client_and_expires_with_its_window()
    {
        var (throttle, clock) = Throttle();
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(throttle.RetryAfter("acct", "c1"));
            throttle.RecordFailure("acct", "c1");
        }

        Assert.Equal(TimeSpan.FromMinutes(15), throttle.RetryAfter("acct", "c1"));
        Assert.Null(throttle.RetryAfter("acct", "c2"));
        Assert.Null(throttle.RetryAfter("other", "c1"));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(5), throttle.RetryAfter("acct", "c1"));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(throttle.RetryAfter("acct", "c1"));
    }

    [Fact]
    public void The_account_limit_blocks_every_client()
    {
        var (throttle, _) = Throttle(perPair: 100, perAccount: 3);
        throttle.RecordFailure("acct", "c1");
        throttle.RecordFailure("acct", "c2");
        throttle.RecordFailure("acct", "c3");

        Assert.NotNull(throttle.RetryAfter("acct", "c-new"));
    }

    [Fact]
    public void Success_clears_the_pair_but_not_the_account_count()
    {
        var (throttle, _) = Throttle(perPair: 2, perAccount: 3);
        throttle.RecordFailure("acct", "c1");
        throttle.RecordFailure("acct", "c1");
        throttle.RecordSuccess("acct", "c1");
        Assert.Null(throttle.RetryAfter("acct", "c1"));

        throttle.RecordFailure("acct", "c1");
        Assert.NotNull(throttle.RetryAfter("acct", "c9")); // 3 failures for the account in this window
    }
}
