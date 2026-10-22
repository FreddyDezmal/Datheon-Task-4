namespace Storefront.Api.Configuration;

/// <summary>Database behaviour (section "Database"). The connection string itself is ConnectionStrings:Storefront.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "Storefront";

    /// <summary>
    /// Apply pending EF Core migrations when the app starts. On in Development for convenience;
    /// off by default elsewhere, where migrations should be applied deliberately (see README).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
