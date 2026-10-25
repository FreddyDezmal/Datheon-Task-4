using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Storefront.Api.Configuration;

namespace Storefront.Api.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Registers the database for the provider named in Database:Provider, with no default and no fallback:
    /// a missing or unknown provider, or a missing or malformed connection string, stops the app at startup.
    /// <list type="bullet">
    /// <item>Sqlite: <see cref="StorefrontDbContext"/> with the SQLite migrations. A relative "Data Source"
    /// is resolved against the app's content root, so the file lands in a predictable place.</item>
    /// <item>PostgreSql: <see cref="PostgreSqlStorefrontDbContext"/> (its own migrations), registered so
    /// everything that asks for a <see cref="StorefrontDbContext"/> (Identity, checkout, health) gets it.</item>
    /// </list>
    /// Error messages never include the connection string: it can contain a password.
    /// </summary>
    public static IServiceCollection AddStorefrontDatabase(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var provider = ReadProvider(configuration);
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' is not configured " +
                $"(Database:Provider is {provider}).");
        }

        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                var sqlite = ResolveSqlitePath(connectionString, environment.ContentRootPath);
                services.AddDbContext<StorefrontDbContext>(options => options.UseSqlite(sqlite));
                break;

            case DatabaseProvider.PostgreSql:
                var postgres = ValidatePostgreSqlConnectionString(connectionString, ReadMaxPoolSize(configuration));
                // No EnableRetryOnFailure: a retrying execution strategy would re-run work on its own and
                // cannot be combined with checkout's explicit transaction. Retries stay with the client,
                // which reuses its Idempotency-Key.
                services.AddDbContext<StorefrontDbContext, PostgreSqlStorefrontDbContext>(options => options.UseNpgsql(postgres));
                break;

            default:
                throw new UnreachableException();
        }

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        return services;
    }

    private sealed class UnreachableException() : InvalidOperationException("Unhandled database provider.");

    internal static DatabaseProvider ReadProvider(IConfiguration configuration)
    {
        var value = configuration.GetSection(DatabaseOptions.SectionName)[nameof(DatabaseOptions.Provider)];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Database:Provider is not configured. Set it to 'PostgreSql' (production) or 'Sqlite' " +
                "(development and tests), e.g. with the environment variable Database__Provider=PostgreSql.");
        }

        if (!Enum.TryParse<DatabaseProvider>(value.Trim(), ignoreCase: true, out var provider)
            || !Enum.IsDefined(provider))
        {
            throw new InvalidOperationException(
                $"Database:Provider '{value}' is not supported. Use 'PostgreSql' or 'Sqlite'.");
        }

        return provider;
    }

    /// <summary>
    /// Database:MaxPoolSize (1–100, default 20): the connection pool's ceiling when the connection string
    /// doesn't set one. Npgsql's own default is 100, which is as many connections as a small managed
    /// PostgreSQL plan allows in total, leaving nothing for a second instance during a deploy or for an
    /// administrator (Security-Audit.md, SA-04).
    /// </summary>
    internal static int ReadMaxPoolSize(IConfiguration configuration)
    {
        var value = configuration.GetValue("Database:MaxPoolSize", DefaultMaxPoolSize);
        return value is >= 1 and <= 100
            ? value
            : throw new InvalidOperationException("Database:MaxPoolSize must be between 1 and 100.");
    }

    internal const int DefaultMaxPoolSize = 20;

    // The spellings Npgsql accepts (it rejects "Max Pool Size").
    private static readonly string[] MaxPoolSizeKeys = ["maximum pool size", "maxpoolsize"];

    internal static string ValidatePostgreSqlConnectionString(string connectionString, int defaultMaxPoolSize = DefaultMaxPoolSize)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} is not a valid PostgreSQL connection string " +
                "(expected e.g. Host=…;Port=5432;Database=…;Username=…;Password=…).");
        }

        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} must name a Host and a Database for PostgreSQL.");
        }

        // An explicit Maximum Pool Size in the connection string wins; otherwise apply the app's ceiling.
        var keys = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }.Keys
            .Cast<string>()
            .Select(k => k.Trim().ToLowerInvariant());
        if (!keys.Any(MaxPoolSizeKeys.Contains))
        {
            builder.MaxPoolSize = defaultMaxPoolSize;
        }

        if (builder.MinPoolSize > builder.MaxPoolSize)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} sets Minimum Pool Size above Maximum Pool Size.");
        }

        return builder.ConnectionString;
    }

    internal static string ResolveSqlitePath(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        var isInMemory = string.IsNullOrEmpty(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory;
        if (isInMemory)
        {
            return builder.ToString();
        }

        if (!Path.IsPathRooted(dataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, dataSource));
        }

        var directory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return builder.ToString();
    }

    /// <summary>Where the app's data lives, for logs: never the password or the full connection string.</summary>
    internal static string DescribeDatabase(StorefrontDbContext db)
    {
        var connectionString = db.Database.GetConnectionString() ?? string.Empty;
        if (db.Database.IsNpgsql())
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"PostgreSQL {builder.Host}:{builder.Port}/{builder.Database} (connection pool up to {builder.MaxPoolSize})";
        }

        return $"SQLite {new SqliteConnectionStringBuilder(connectionString).DataSource}";
    }

    /// <summary>Applies pending migrations when Database:ApplyMigrationsOnStartup is true.</summary>
    public static async Task ApplyMigrationsIfConfiguredAsync(this WebApplication app)
    {
        var options = app.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();
        if (db.Database.IsSqlite() && !app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning(
                "Database:Provider is Sqlite outside Development ({Environment}). SQLite suits a single machine only; " +
                "production deployments should use PostgreSql.", app.Environment.EnvironmentName);
        }

        if (!options.ApplyMigrationsOnStartup)
        {
            app.Logger.LogInformation("Using {Database}. Migrations are not applied at startup.", DescribeDatabase(db));
            return;
        }

        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database is up to date ({Database}).", DescribeDatabase(db));
    }
}
