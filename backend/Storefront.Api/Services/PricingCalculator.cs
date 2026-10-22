using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

public interface IPricingCalculator
{
    PriceBreakdown Calculate(IEnumerable<PricingLine> lines);
}

/// <summary>
/// Order pricing with decimal arithmetic. Policy (mirrored exactly by the frontend):
/// <list type="bullet">
///   <item>Unit prices are rounded to 2 dp, then line total = unit price × quantity.</item>
///   <item>Subtotal = sum of line totals (merchandise only).</item>
///   <item>Tax = subtotal × tax rate, rounded once to 2 dp. Shipping is not taxed.</item>
///   <item>Shipping = flat fee when 0 &lt; subtotal &lt; threshold; free at or above the threshold; 0 for an empty order.</item>
///   <item>Total = subtotal + tax + shipping.</item>
/// </list>
/// Rounding is half away from zero, matching JavaScript's Math.round for positive amounts.
/// </summary>
public sealed class PricingCalculator(IOptions<PricingOptions> options) : IPricingCalculator
{
    private readonly PricingOptions _options = options.Value;

    public PriceBreakdown Calculate(IEnumerable<PricingLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var priced = new List<PricedLine>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lines), "Quantities must be positive.");
            }

            if (line.UnitPrice < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lines), "Unit prices cannot be negative.");
            }

            var unitPrice = RoundMoney(line.UnitPrice);
            priced.Add(new PricedLine(line.ProductId, line.Title, unitPrice, line.Quantity, unitPrice * line.Quantity));
        }

        var subtotal = priced.Sum(l => l.LineTotal);
        var tax = RoundMoney(subtotal * _options.TaxRate);
        var shipping = CalculateShipping(subtotal);

        return new PriceBreakdown(priced, subtotal, tax, shipping, subtotal + tax + shipping, _options.Currency);
    }

    private decimal CalculateShipping(decimal subtotal) =>
        subtotal > 0 && subtotal < _options.FreeShippingThreshold ? _options.FlatShippingFee : 0m;

    public static decimal RoundMoney(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static long ToMinorUnits(decimal amount) => (long)(RoundMoney(amount) * 100m);
}
