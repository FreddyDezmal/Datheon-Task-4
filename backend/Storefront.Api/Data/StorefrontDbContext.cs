using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data.Configurations;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Data;

/// <summary>
/// The application's database: Identity's user tables, orders, and the Data Protection key ring.
/// The product catalogue is deliberately not stored here; FakeStoreAPI remains its source of truth.
///
/// This type is used with SQLite, and its migrations (Data/Migrations) are SQLite migrations.
/// PostgreSQL uses <see cref="PostgreSqlStorefrontDbContext"/>: the same model, with its own migration
/// set, because migrations generated for one provider aren't portable to another.
/// </summary>
public class StorefrontDbContext : IdentityDbContext<StorefrontUser>, IDataProtectionKeyContext
{
    public StorefrontDbContext(DbContextOptions<StorefrontDbContext> options)
        : base(options)
    {
    }

    /// <summary>For provider-specific subclasses, which need options typed to themselves.</summary>
    protected StorefrontDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    /// <summary>Signed-in sessions, so tokens can be revoked (sign-out, refresh-token reuse, password change).</summary>
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>
    /// The keys that sign Identity's bearer and refresh tokens. Kept here rather than on the server's
    /// disk, so a new container (every deploy) and every instance use the same keys, and sign-ins
    /// survive. Anyone who can read this table can forge tokens: protect it like the password hashes.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Each context type (SQLite, PostgreSQL) gets its own cached model, so this is decided once per provider.
        var isSqlite = Database.IsSqlite();
        builder.ApplyConfiguration(new OrderConfiguration(isSqlite));
        builder.ApplyConfiguration(new OrderLineConfiguration(isSqlite));
        builder.ApplyConfiguration(new UserSessionConfiguration());
    }
}

/// <summary>
/// <see cref="StorefrontDbContext"/> on PostgreSQL. A separate type only so that EF Core keeps its
/// migrations (Data/Migrations/PostgreSql) apart from the SQLite ones; the model is identical.
/// </summary>
public sealed class PostgreSqlStorefrontDbContext(DbContextOptions<PostgreSqlStorefrontDbContext> options)
    : StorefrontDbContext(options);
