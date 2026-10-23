using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Storefront.Api.Data;

/// <summary>
/// Used only by `dotnet ef` for <see cref="PostgreSqlStorefrontDbContext"/>. Creating a migration needs no
/// database, so this placeholder is never connected to. Commands that do connect (database update,
/// migrations script with --idempotent against a live database) must pass --connection explicitly;
/// see Documentation/Deployment.md. Never put a real connection string here.
/// </summary>
public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<PostgreSqlStorefrontDbContext>
{
    public PostgreSqlStorefrontDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgreSqlStorefrontDbContext>()
            .UseNpgsql("Host=localhost;Database=storefront_design_time_placeholder")
            .Options);
}
