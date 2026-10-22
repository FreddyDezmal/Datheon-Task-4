using Storefront.Api.DTOs;
using Storefront.Api.Exceptions;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

public interface ICheckoutService
{
    Task<CheckoutConfirmationDto> CheckoutAsync(CheckoutRequestDto request, CancellationToken cancellationToken);
}

/// <summary>
/// Simulated checkout: validates the request, re-prices every line from the server-side
/// catalogue, and returns a confirmation. No payment is taken and nothing is persisted.
/// </summary>
public sealed class CheckoutService(
    ICheckoutRequestValidator validator,
    IProductCatalogService catalogService,
    IPricingCalculator pricingCalculator,
    IOrderReferenceGenerator referenceGenerator,
    TimeProvider timeProvider,
    ILogger<CheckoutService> logger) : ICheckoutService
{
    public const string SimulatedNotice =
        "This is a simulated checkout. No payment was taken and the order has not been stored.";

    public async Task<CheckoutConfirmationDto> CheckoutAsync(
        CheckoutRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = validator.Validate(request);
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var catalogue = await catalogService.GetCatalogueAsync(cancellationToken);
        var items = request.Items!;

        var missing = items.Where(i => !catalogue.ContainsKey(i.ProductId)).Select(i => i.ProductId).ToList();
        if (missing.Count > 0)
        {
            throw new ProductsUnavailableException(missing);
        }

        // Prices always come from the catalogue, never from the request.
        var breakdown = pricingCalculator.Calculate(items.Select(i =>
        {
            var product = catalogue[i.ProductId];
            return new PricingLine(product.Id, product.Title, product.Price, i.Quantity);
        }));

        if (request.ExpectedTotal is { } expected && PricingCalculator.RoundMoney(expected) != breakdown.Total)
        {
            throw new PriceChangedException(expected, breakdown);
        }

        var reference = referenceGenerator.Next();
        logger.LogInformation(
            "Simulated order {OrderReference} accepted: {LineCount} lines, total {Total} {Currency}.",
            reference, breakdown.Lines.Count, breakdown.Total, breakdown.Currency);

        return new CheckoutConfirmationDto(
            reference,
            timeProvider.GetUtcNow(),
            request.Customer!.Name!.Trim(),
            request.Customer.Email!.Trim(),
            ToLineDtos(breakdown),
            ToTotalsDto(breakdown),
            IsSimulated: true,
            SimulatedNotice);
    }

    public static IReadOnlyList<OrderLineDto> ToLineDtos(PriceBreakdown breakdown) =>
        breakdown.Lines
            .Select(l => new OrderLineDto(
                l.ProductId, l.Title, l.UnitPrice, PricingCalculator.ToMinorUnits(l.UnitPrice), l.Quantity, l.LineTotal))
            .ToList();

    public static OrderTotalsDto ToTotalsDto(PriceBreakdown breakdown) =>
        new(breakdown.Subtotal, breakdown.Tax, breakdown.Shipping, breakdown.Total, breakdown.Currency);
}
