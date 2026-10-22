namespace Storefront.Api.Data.Entities;

/// <summary>
/// A placed order. Amounts are snapshots calculated by the server at checkout time, so the
/// order stays historically correct even if catalogue prices or titles change later.
/// </summary>
public sealed class Order
{
    public long Id { get; set; }

    /// <summary>Public reference shown to the customer, e.g. "DEMO-20261022-7KQ2XM". Unique.</summary>
    public required string Reference { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The owning account, or null for a guest checkout. Always taken from the auth token.</summary>
    public string? UserId { get; set; }

    public StorefrontUser? User { get; set; }

    public required string CustomerName { get; set; }

    public required string CustomerEmail { get; set; }

    public required string Currency { get; set; }

    public decimal Subtotal { get; set; }

    public decimal Tax { get; set; }

    public decimal Shipping { get; set; }

    public decimal Total { get; set; }

    /// <summary>Client-supplied Idempotency-Key header, if any. Unique, so a retry can't create a second order.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>SHA-256 of the order's customer and items, used to detect a key reused for a different order.</summary>
    public string? RequestFingerprint { get; set; }

    public List<OrderLine> Lines { get; } = [];
}
