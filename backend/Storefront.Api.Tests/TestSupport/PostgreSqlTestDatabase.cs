using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Storefront.Api.Data;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// A brand-new PostgreSQL database for one test, created on the server named by the environment variable
/// STOREFRONT_TEST_POSTGRES and dropped afterwards. The variable is a connection string for an account
/// allowed to create databases, on a server used for nothing else, for example:
/// <code>Host=127.0.0.1;Port=5432;Username=postgres;Password=…</code>
/// Tests never touch a database they didn't create, so the server's other data is left alone, but
/// don't point this at a production server.
/// </summary>
internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "STOREFRONT_TEST_POSTGRES";

    private readonly string _adminConnectionString;

    private PostgreSqlTestDatabase(string adminConnectionString, string name)
    {
        _adminConnectionString = adminConnectionString;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    public static string? AdminConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public string Name { get; }

    /// <summary>Connection string for this test's database (pooling off, so dropping it is never blocked).</summary>
    public string ConnectionString { get; }

    /// <summary>Settings that make <see cref="Integration.StorefrontApiFactory"/> run the API on this database.</summary>
    public Dictionary<string, string?> AppSettings(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "PostgreSql",
            ["ConnectionStrings:Storefront"] = ConnectionString,
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>Creates an empty database. <paramref name="migrate"/> applies the PostgreSQL migrations.</summary>
    public static async Task<PostgreSqlTestDatabase> CreateAsync(bool migrate = true)
    {
        var admin = AdminConnectionString
            ?? throw new InvalidOperationException($"{EnvironmentVariable} is not set.");
        var database = new PostgreSqlTestDatabase(admin, $"storefront_test_{Guid.NewGuid():N}");

        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database.Name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        if (migrate)
        {
            await using var db = database.CreateContext();
            await db.Database.MigrateAsync();
        }

        return database;
    }

    public PostgreSqlStorefrontDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PostgreSqlStorefrontDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>A fact that runs only when a disposable PostgreSQL server is configured, and is skipped (with the reason) otherwise.</summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgreSqlTestDatabase.AdminConnectionString))
        {
            Skip = $"Set {PostgreSqlTestDatabase.EnvironmentVariable} to a disposable PostgreSQL server to run this test.";
        }
    }
}

/// <summary>A theory that runs only when a disposable PostgreSQL server is configured.</summary>
public sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgreSqlTestDatabase.AdminConnectionString))
        {
            Skip = $"Set {PostgreSqlTestDatabase.EnvironmentVariable} to a disposable PostgreSQL server to run this test.";
        }
    }
}
