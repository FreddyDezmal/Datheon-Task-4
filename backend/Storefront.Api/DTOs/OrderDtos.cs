namespace Storefront.Api.DTOs;

/// <summary>One row of GET /api/orders.</summary>
public sealed record OrderSummaryDto(
    string OrderReference,
    DateTimeOffset CreatedAt,
    int ItemCount,
    decimal Total,
    string Currency);

/// <summary>GET /api/orders/{reference}: the stored order exactly as it was priced at checkout.</summary>
public sealed record OrderDto(
    string OrderReference,
    DateTimeOffset CreatedAt,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<OrderLineDto> Lines,
    OrderTotalsDto Totals);
