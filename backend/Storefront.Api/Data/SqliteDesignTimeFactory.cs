using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Storefront.Api.Data;

/// <summary>
/// Used only by `dotnet ef` for the SQLite <see cref="StorefrontDbContext"/>, with the same database file
/// as Development (App_Data/storefront.db in this project).
///
/// Required, not optional: <see cref="IDesignTimeDbContextFactory{TContext}"/> is covariant and the
/// PostgreSQL context derives from this one, so without an exact factory here EF would pick
/// <see cref="PostgreSqlDesignTimeFactory"/> for SQLite commands too.
/// </summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<StorefrontDbContext>
{
    public StorefrontDbContext CreateDbContext(string[] args)
    {
        var connectionString = DatabaseSetup.ResolveSqlitePath("Data Source=App_Data/storefront.db", Directory.GetCurrentDirectory());
        return new(new DbContextOptionsBuilder<StorefrontDbContext>().UseSqlite(connectionString).Options);
    }
}
