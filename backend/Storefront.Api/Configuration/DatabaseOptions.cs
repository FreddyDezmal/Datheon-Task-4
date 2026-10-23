namespace Storefront.Api.Configuration;

/// <summary>Which database engine the app talks to.</summary>
public enum DatabaseProvider
{
    /// <summary>A local file. For development, tests and single-machine demos.</summary>
    Sqlite,

    /// <summary>PostgreSQL. For production.</summary>
    PostgreSql,
}

/// <summary>
/// Database behaviour (section "Database"). The connection string itself is ConnectionStrings:Storefront.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "Storefront";

    /// <summary>
    /// Required, with no default: "Sqlite" or "PostgreSql". appsettings.Development.json sets Sqlite; every
    /// other environment must set it explicitly (e.g. Database__Provider=PostgreSql), so a production
    /// deployment that is missing its settings fails at startup instead of quietly using a local file.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Apply pending EF Core migrations when the app starts. On in Development for convenience;
    /// off by default elsewhere, where migrations should be applied deliberately (see README).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
