using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Data.Configurations;

public sealed class OrderConfiguration(bool isSqlite) : IEntityTypeConfiguration<Order>
{
    /// <summary>
    /// "column >= 0" for a decimal column. SQLite stores decimals as TEXT, so it must CAST to compare
    /// numerically; PostgreSQL stores them as exact numeric and compares them directly.
    /// </summary>
    internal static string NonNegative(string column, bool isSqlite) =>
        isSqlite ? $"CAST(\"{column}\" AS REAL) >= 0" : $"\"{column}\" >= 0";

    public const int ReferenceMaxLength = 32;
    public const int IdempotencyKeyMaxLength = 100;

    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable("Orders", table =>
        {
            table.HasCheckConstraint("CK_Orders_Subtotal_NonNegative", NonNegative("Subtotal", isSqlite));
            table.HasCheckConstraint("CK_Orders_Tax_NonNegative", NonNegative("Tax", isSqlite));
            table.HasCheckConstraint("CK_Orders_Shipping_NonNegative", NonNegative("Shipping", isSqlite));
            table.HasCheckConstraint("CK_Orders_Total_NonNegative", NonNegative("Total", isSqlite));
        });

        order.Property(o => o.Reference).HasMaxLength(ReferenceMaxLength);
        order.HasIndex(o => o.Reference).IsUnique();

        order.Property(o => o.CustomerName).HasMaxLength(100);
        order.Property(o => o.CustomerEmail).HasMaxLength(254);
        order.Property(o => o.Currency).HasMaxLength(3);

        order.Property(o => o.IdempotencyKey).HasMaxLength(IdempotencyKeyMaxLength);
        order.HasIndex(o => o.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        order.Property(o => o.RequestFingerprint).HasMaxLength(64);

        // Always UTC. SQLite has no time zone type, so values are read back marked as UTC; PostgreSQL
        // stores timestamp with time zone, and Npgsql only accepts DateTimeKind.Utc for it.
        order.Property(o => o.CreatedAtUtc)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        // "My orders" lists filter by user and sort newest first.
        order.HasIndex(o => new { o.UserId, o.CreatedAtUtc });

        // Deleting an account keeps its orders (as historical records) but detaches them.
        order.HasOne(o => o.User)
            .WithMany(u => u.Orders)
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        order.HasMany(o => o.Lines)
            .WithOne(l => l.Order)
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
