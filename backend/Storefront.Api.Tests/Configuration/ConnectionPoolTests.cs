using Microsoft.Extensions.Configuration;
using Npgsql;
using Storefront.Api.Data;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Configuration;

/// <summary>
/// The PostgreSQL connection pool is bounded by the app, not left at Npgsql's 100, which is a small
/// managed plan's whole connection limit (Security-Audit.md, SA-04).
/// </summary>
[Trait("Category", TestCategories.Unit)]
public sealed class ConnectionPoolTests
{
    private const string Base = "Host=db.internal;Port=5432;Database=storefront;Username=app;Password=pw-123";

    private static int PoolOf(string connectionString, int? configured = null)
    {
        var size = configured is null
            ? DatabaseSetup.ReadMaxPoolSize(new ConfigurationBuilder().Build())
            : DatabaseSetup.ReadMaxPoolSize(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:MaxPoolSize"] = configured.ToString() }).Build());
        return new NpgsqlConnectionStringBuilder(DatabaseSetup.ValidatePostgreSqlConnectionString(connectionString, size)).MaxPoolSize;
    }

    [Fact]
    public void Without_a_setting_the_pool_is_capped_at_20() => Assert.Equal(20, PoolOf(Base));

    [Fact]
    public void Database_MaxPoolSize_sets_the_cap() => Assert.Equal(7, PoolOf(Base, configured: 7));

    [Theory]
    [InlineData(";Maximum Pool Size=50", 50)]
    [InlineData(";MaxPoolSize=4", 4)]
    [InlineData("; maximum pool size = 3 ", 3)]
    public void An_explicit_value_in_the_connection_string_wins(string suffix, int expected) =>
        Assert.Equal(expected, PoolOf(Base + suffix, configured: 7));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void Out_of_range_caps_stop_startup(int value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:MaxPoolSize"] = value.ToString() }).Build();
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.ReadMaxPoolSize(configuration));
    }

    [Fact]
    public void A_minimum_above_the_maximum_is_refused_without_echoing_the_password()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseSetup.ValidatePostgreSqlConnectionString(Base + ";Minimum Pool Size=30"));
        Assert.DoesNotContain("pw-123", error.Message);
    }
}
