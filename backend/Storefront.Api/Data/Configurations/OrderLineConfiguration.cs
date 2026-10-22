using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Data.Configurations;

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> line)
    {
        line.ToTable("OrderLines", table =>
        {
            table.HasCheckConstraint("CK_OrderLines_Quantity_Positive", "\"Quantity\" > 0");
            table.HasCheckConstraint("CK_OrderLines_ProductId_Positive", "\"ProductId\" > 0");
            table.HasCheckConstraint("CK_OrderLines_UnitPrice_NonNegative", "CAST(\"UnitPrice\" AS REAL) >= 0");
            table.HasCheckConstraint("CK_OrderLines_LineTotal_NonNegative", "CAST(\"LineTotal\" AS REAL) >= 0");
        });

        line.Property(l => l.Title).HasMaxLength(500);

        // One line per product per order, matching the checkout validator's "no duplicates" rule.
        line.HasIndex(l => new { l.OrderId, l.ProductId }).IsUnique();
    }
}
