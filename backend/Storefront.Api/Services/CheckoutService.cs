using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;
using Storefront.Api.DTOs;
using Storefront.Api.Exceptions;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

public interface ICheckoutService
{
    Task<CheckoutResult> CheckoutAsync(CheckoutRequestDto request, CheckoutCaller caller, CancellationToken cancellationToken);
}

/// <summary>
/// Places an order: validates the request, re-prices every line from the server-side catalogue,
/// and saves the order and its lines in one database transaction. No payment is taken.
///
/// With an Idempotency-Key, a retried request returns the order already created instead of
/// creating another; the unique index on the key settles concurrent duplicates.
/// </summary>
public sealed class CheckoutService(
    ICheckoutRequestValidator validator,
    IProductCatalogService catalogService,
    IPricingCalculator pricingCalculator,
    IOrderReferenceGenerator referenceGenerator,
    StorefrontDbContext db,
    TimeProvider timeProvider,
    ILogger<CheckoutService> logger) : ICheckoutService
{
    public const string SimulatedNotice =
        "This is a demo checkout. No payment was taken. Your order has been saved.";

    private const int MaxReferenceAttempts = 3;

    public async Task<CheckoutResult> CheckoutAsync(
        CheckoutRequestDto request, CheckoutCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var errors = validator.Validate(request);
        if (caller.IdempotencyKey is { } key && !IdempotencyKeys.IsValid(key))
        {
            errors[IdempotencyKeys.HeaderName] =
                ["Idempotency-Key must be 8-100 characters: letters, digits, '-' or '_'."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var fingerprint = IdempotencyKeys.Fingerprint(request);
        if (await FindReplayAsync(caller, fingerprint, cancellationToken) is { } replay)
        {
            return replay;
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

        var order = CreateOrder(request, caller, fingerprint, breakdown);
        for (var attempt = 1; ; attempt++)
        {
            db.Orders.Add(order);
            try
            {
                // One SaveChanges = one transaction: the order and all its lines are saved, or none are.
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();

                // A concurrent request with the same Idempotency-Key won the race: return its order.
                if (await FindReplayAsync(caller, fingerprint, cancellationToken) is { } concurrent)
                {
                    return concurrent;
                }

                // Otherwise the random order reference collided; pick another and retry.
                if (attempt >= MaxReferenceAttempts)
                {
                    throw;
                }

                order = CreateOrder(request, caller, fingerprint, breakdown);
            }
        }

        logger.LogInformation(
            "Order {OrderReference} saved: {LineCount} lines, total {Total} {Currency}, {CustomerType}.",
            order.Reference, order.Lines.Count, order.Total, order.Currency, caller.UserId is null ? "guest" : "signed-in");

        return new CheckoutResult(OrderMapping.ToConfirmation(order), IsReplay: false);
    }

    private async Task<CheckoutResult?> FindReplayAsync(
        CheckoutCaller caller, string fingerprint, CancellationToken cancellationToken)
    {
        if (caller.IdempotencyKey is not { } key)
        {
            return null;
        }

        var existing = await db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.IdempotencyKey == key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        // The same key must come from the same caller for the same order; otherwise a key
        // could be used to read someone else's confirmation.
        if (existing.RequestFingerprint != fingerprint || existing.UserId != caller.UserId)
        {
            throw new IdempotencyKeyReusedException();
        }

        logger.LogInformation("Order {OrderReference} returned again for a repeated Idempotency-Key.", existing.Reference);
        return new CheckoutResult(OrderMapping.ToConfirmation(existing), IsReplay: true);
    }

    private Order CreateOrder(
        CheckoutRequestDto request, CheckoutCaller caller, string fingerprint, PriceBreakdown breakdown)
    {
        var order = new Order
        {
            Reference = referenceGenerator.Next(),
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            UserId = caller.UserId,
            CustomerName = request.Customer!.Name!.Trim(),
            CustomerEmail = request.Customer.Email!.Trim(),
            Currency = breakdown.Currency,
            Subtotal = breakdown.Subtotal,
            Tax = breakdown.Tax,
            Shipping = breakdown.Shipping,
            Total = breakdown.Total,
            IdempotencyKey = caller.IdempotencyKey,
            RequestFingerprint = caller.IdempotencyKey is null ? null : fingerprint,
        };

        order.Lines.AddRange(breakdown.Lines.Select(l => new OrderLine
        {
            ProductId = l.ProductId,
            Title = l.Title,
            UnitPrice = l.UnitPrice,
            Quantity = l.Quantity,
            LineTotal = l.LineTotal,
        }));

        return order;
    }

    public static IReadOnlyList<OrderLineDto> ToLineDtos(PriceBreakdown breakdown) =>
        breakdown.Lines
            .Select(l => new OrderLineDto(
                l.ProductId, l.Title, l.UnitPrice, PricingCalculator.ToMinorUnits(l.UnitPrice), l.Quantity, l.LineTotal))
            .ToList();

    public static OrderTotalsDto ToTotalsDto(PriceBreakdown breakdown) =>
        new(breakdown.Subtotal, breakdown.Tax, breakdown.Shipping, breakdown.Total, breakdown.Currency);
}
