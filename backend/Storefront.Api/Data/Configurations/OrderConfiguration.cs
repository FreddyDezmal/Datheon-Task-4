using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Data.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public const int ReferenceMaxLength = 32;
    public const int IdempotencyKeyMaxLength = 100;

    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable("Orders", table =>
        {
            // SQLite stores decimals as TEXT, so numeric checks must CAST to compare correctly.
            table.HasCheckConstraint("CK_Orders_Subtotal_NonNegative", "CAST(\"Subtotal\" AS REAL) >= 0");
            table.HasCheckConstraint("CK_Orders_Tax_NonNegative", "CAST(\"Tax\" AS REAL) >= 0");
            table.HasCheckConstraint("CK_Orders_Shipping_NonNegative", "CAST(\"Shipping\" AS REAL) >= 0");
            table.HasCheckConstraint("CK_Orders_Total_NonNegative", "CAST(\"Total\" AS REAL) >= 0");
        });

        order.Property(o => o.Reference).HasMaxLength(ReferenceMaxLength);
        order.HasIndex(o => o.Reference).IsUnique();

        order.Property(o => o.CustomerName).HasMaxLength(100);
        order.Property(o => o.CustomerEmail).HasMaxLength(254);
        order.Property(o => o.Currency).HasMaxLength(3);

        order.Property(o => o.IdempotencyKey).HasMaxLength(IdempotencyKeyMaxLength);
        order.HasIndex(o => o.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        order.Property(o => o.RequestFingerprint).HasMaxLength(64);

        // SQLite has no time zone type; values are written as UTC and read back marked as UTC.
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
