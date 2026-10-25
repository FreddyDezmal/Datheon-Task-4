using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Storefront.Api.Data;
using Storefront.Api.Middleware;
using Storefront.Api.Tests.Integration;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Configuration;

/// <summary>Provider selection and PostgreSQL error handling. No database server needed.</summary>
[Trait("Category", TestCategories.Unit)]
public sealed class DatabaseProviderTests
{
    private const string Secret = "s3cret-Pa55";
    private static readonly string PostgresConnection = $"Host=db.internal;Port=5432;Database=storefront;Username=app;Password={Secret}";

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Storefront.Api";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddStorefrontDatabase(configuration, new Env());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_provider_stops_startup_instead_of_falling_back(string? provider)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Database:Provider"] = provider,
            ["ConnectionStrings:Storefront"] = "Data Source=x.db",
        }));

        Assert.Contains("Database:Provider is not configured", error.Message);
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("InMemory")]
    [InlineData("2")]
    public void An_unknown_provider_is_rejected(string provider)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Database:Provider"] = provider,
            ["ConnectionStrings:Storefront"] = "Data Source=x.db",
        }));

        Assert.Contains("is not supported", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not=a;valid connection")]
    [InlineData("Host=db.internal;Password=" + Secret)] // no Database
    public void A_bad_postgresql_connection_string_stops_startup_without_echoing_it(string? connectionString)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Database:Provider"] = "PostgreSql",
            ["ConnectionStrings:Storefront"] = connectionString,
        }));

        Assert.DoesNotContain(Secret, error.Message);
        Assert.DoesNotContain("db.internal", error.Message);
    }

    [Fact]
    public void PostgreSql_registers_the_postgresql_context_for_everything_that_asks_for_the_database()
    {
        using var provider = Build(new() { ["Database:Provider"] = "postgresql", ["ConnectionStrings:Storefront"] = PostgresConnection });
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();

        Assert.IsType<PostgreSqlStorefrontDbContext>(db);
        Assert.True(db.Database.IsNpgsql());
        // No retrying execution strategy: it can't be combined with checkout's explicit transaction,
        // and would re-run database work on its own.
        Assert.False(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.Equal("PostgreSQL db.internal:5432/storefront (connection pool up to 20)", DatabaseSetup.DescribeDatabase(db));
    }

    [Fact]
    public void Sqlite_registers_the_sqlite_context_as_before()
    {
        using var provider = Build(new() { ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Storefront"] = "Data Source=:memory:" });
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();

        Assert.Equal(typeof(StorefrontDbContext), db.GetType());
        Assert.True(db.Database.IsSqlite());
    }

    [Fact]
    public void The_shared_settings_file_names_no_provider_and_no_database_so_production_must_configure_both()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        Assert.False(json.RootElement.TryGetProperty("ConnectionStrings", out _));
        Assert.False(json.RootElement.GetProperty("Database").TryGetProperty("Provider", out _));
    }

    [Fact]
    public void The_app_does_not_start_without_a_provider()
    {
        using var factory = new StorefrontApiFactory(settings: new Dictionary<string, string?> { ["Database:Provider"] = "" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Database:Provider is not configured", error.ToString());
    }

    [Fact]
    public void The_postgresql_model_matches_its_migrations()
    {
        using var db = new PostgreSqlDesignTimeFactory().CreateDbContext([]);

        Assert.False(db.Database.HasPendingModelChanges(),
            "Run: dotnet ef migrations add <Name> --project Storefront.Api --context PostgreSqlStorefrontDbContext --output-dir Data/Migrations/PostgreSql");
        Assert.Contains(db.Database.GetMigrations(), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_two_migration_sets_stay_apart()
    {
        using var postgres = new PostgreSqlDesignTimeFactory().CreateDbContext([]);
        using var sqlite = new StorefrontDbContext(new DbContextOptionsBuilder<StorefrontDbContext>().UseSqlite("Data Source=:memory:").Options);

        var pgTypes = postgres.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>().Migrations.Values.Select(t => t.Namespace).Distinct();
        var sqliteTypes = sqlite.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>().Migrations.Values.Select(t => t.Namespace).Distinct();

        Assert.Equal(["Storefront.Api.Data.Migrations.PostgreSql"], pgTypes);
        Assert.Equal(["Storefront.Api.Data.Migrations"], sqliteTypes);
    }

    // ---------- PostgreSQL errors ----------

    private static PostgresException Pg(string sqlState) => new("Injected.", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("40001", "DATABASE_BUSY", 503)]       // serialization failure
    [InlineData("40P01", "DATABASE_BUSY", 503)]       // deadlock
    [InlineData("55P03", "DATABASE_BUSY", 503)]       // lock not available
    [InlineData("57P01", "DATABASE_UNAVAILABLE", 503)] // admin shutdown
    [InlineData("57P03", "DATABASE_UNAVAILABLE", 503)] // cannot connect now
    [InlineData("53300", "DATABASE_UNAVAILABLE", 503)] // too many connections
    [InlineData("3D000", "DATABASE_UNAVAILABLE", 503)] // database does not exist
    [InlineData("28P01", "DATABASE_UNAVAILABLE", 503)] // wrong password
    [InlineData("08006", "DATABASE_UNAVAILABLE", 503)] // connection failure
    [InlineData("23514", "CONSTRAINT_CONFLICT", 409)]  // check violation
    [InlineData("22003", "DATABASE_ERROR", 500)]       // numeric out of range: not transient, not definite
    public void PostgreSql_errors_map_to_the_same_codes_as_their_sqlite_equivalents(string sqlState, string code, int status)
    {
        var problem = GlobalExceptionHandler.Map(new DbUpdateException("Saving failed.", Pg(sqlState)));

        Assert.Equal(status, problem.Status);
        Assert.Equal(code, problem.Extensions["code"]);
    }

    [Fact]
    public void A_connection_that_never_got_an_answer_is_unavailable()
    {
        var lost = new NpgsqlException("Failed to connect", new System.Net.Sockets.SocketException(111));

        Assert.True(DatabaseErrors.IsUnavailable(lost));
        Assert.Equal("DATABASE_UNAVAILABLE", GlobalExceptionHandler.Map(lost).Extensions["code"]);
    }

    [Fact]
    public void A_postgresql_unique_violation_is_recognised_for_the_idempotency_race()
    {
        Assert.True(DatabaseErrors.IsUniqueViolation(new DbUpdateException("dup", Pg("23505"))));
        Assert.False(DatabaseErrors.IsUniqueViolation(new DbUpdateException("check", Pg("23514"))));
    }

    [Fact]
    public void A_commit_failure_stays_an_unknown_outcome_on_postgresql_too()
    {
        foreach (var inner in new Exception[] { Pg("40001"), Pg("57P01"), new NpgsqlException("reset", new IOException()) })
        {
            var problem = GlobalExceptionHandler.Map(new Storefront.Api.Exceptions.OrderOutcomeUnknownException(inner));

            Assert.Equal(500, problem.Status);
            Assert.Equal("DATABASE_ERROR", problem.Extensions["code"]);
        }
    }
}
