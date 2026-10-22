using System.ComponentModel.DataAnnotations;

namespace Storefront.Api.DTOs;

/// <summary>
/// Body of POST /api/checkout. Deliberately carries no prices for individual items:
/// the server looks them up itself. <see cref="ExpectedTotal"/> is only used to detect
/// that the shopper saw a different total than the one the server calculates.
/// </summary>
public sealed class CheckoutRequestDto
{
    [Required]
    public CustomerDto? Customer { get; set; }

    [Required]
    public List<CheckoutItemDto>? Items { get; set; }

    /// <summary>The total the shopper was shown. Optional; when present and different, the API returns 409.</summary>
    public decimal? ExpectedTotal { get; set; }
}

public sealed class CustomerDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

public sealed class CheckoutItemDto
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

public sealed record OrderLineDto(
    int ProductId,
    string Title,
    decimal UnitPrice,
    long UnitPriceMinor,
    int Quantity,
    decimal LineTotal);

public sealed record OrderTotalsDto(decimal Subtotal, decimal Tax, decimal Shipping, decimal Total, string Currency);

/// <summary>A server-calculated price quote, returned when the client's expected total is stale.</summary>
public sealed record OrderQuoteDto(IReadOnlyList<OrderLineDto> Lines, OrderTotalsDto Totals);

public sealed record CheckoutConfirmationDto(
    string OrderReference,
    DateTimeOffset CreatedAt,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<OrderLineDto> Lines,
    OrderTotalsDto Totals,
    bool IsSimulated,
    string Notice);
