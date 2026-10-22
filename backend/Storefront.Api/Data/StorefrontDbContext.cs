using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Data;

/// <summary>
/// The application's database: Identity's user tables plus orders.
/// The product catalogue is deliberately not stored here; FakeStoreAPI remains its source of truth.
/// </summary>
public sealed class StorefrontDbContext(DbContextOptions<StorefrontDbContext> options)
    : IdentityDbContext<StorefrontUser>(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(StorefrontDbContext).Assembly);
    }
}
