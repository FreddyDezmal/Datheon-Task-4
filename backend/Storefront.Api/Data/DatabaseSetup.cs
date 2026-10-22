using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Storefront.Api.Configuration;

namespace Storefront.Api.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Registers <see cref="StorefrontDbContext"/> with SQLite. A relative "Data Source" is resolved
    /// against the app's content root (the Storefront.Api folder), so the database file lands in a
    /// predictable place no matter which directory the app is started from.
    /// </summary>
    public static IServiceCollection AddStorefrontDatabase(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' is not configured.");

        var resolved = ResolveSqlitePath(connectionString, environment.ContentRootPath);
        services.AddDbContext<StorefrontDbContext>(options => options.UseSqlite(resolved));
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        return services;
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

    /// <summary>Applies pending migrations when Database:ApplyMigrationsOnStartup is true.</summary>
    public static async Task ApplyMigrationsIfConfiguredAsync(this WebApplication app)
    {
        var options = app.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (!options.ApplyMigrationsOnStartup)
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database is up to date ({DataSource}).",
            new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource);
    }
}
