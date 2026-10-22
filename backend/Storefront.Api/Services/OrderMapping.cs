using Storefront.Api.Data.Entities;
using Storefront.Api.DTOs;

namespace Storefront.Api.Services;

/// <summary>Converts stored orders to API DTOs. Amounts are read back exactly as they were saved.</summary>
public static class OrderMapping
{
    public static IReadOnlyList<OrderLineDto> ToLineDtos(Order order) =>
        order.Lines
            .OrderBy(l => l.Id)
            .Select(l => new OrderLineDto(
                l.ProductId, l.Title, l.UnitPrice, PricingCalculator.ToMinorUnits(l.UnitPrice), l.Quantity, l.LineTotal))
            .ToList();

    public static OrderTotalsDto ToTotalsDto(Order order) =>
        new(order.Subtotal, order.Tax, order.Shipping, order.Total, order.Currency);

    public static DateTimeOffset CreatedAt(Order order) =>
        new(DateTime.SpecifyKind(order.CreatedAtUtc, DateTimeKind.Utc));

    public static CheckoutConfirmationDto ToConfirmation(Order order) => new(
        order.Reference,
        CreatedAt(order),
        order.CustomerName,
        order.CustomerEmail,
        ToLineDtos(order),
        ToTotalsDto(order),
        IsSimulated: true,
        CheckoutService.SimulatedNotice);

    public static OrderDto ToOrderDto(Order order) => new(
        order.Reference,
        CreatedAt(order),
        order.CustomerName,
        order.CustomerEmail,
        ToLineDtos(order),
        ToTotalsDto(order));
}
