namespace Storefront.Api.Models;

/// <summary>A line to be priced: the unit price must come from the server-side catalogue.</summary>
public sealed record PricingLine(int ProductId, string Title, decimal UnitPrice, int Quantity);

public sealed record PricedLine(int ProductId, string Title, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record PriceBreakdown(
    IReadOnlyList<PricedLine> Lines,
    decimal Subtotal,
    decimal Tax,
    decimal Shipping,
    decimal Total,
    string Currency);
