using System.Globalization;
using Storefront.Api.Models;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Pricing;

public class PricingCalculatorTests
{
    private readonly PricingCalculator _calculator = new(TestData.PricingOptions());

    private static PricingLine Line(int id, decimal price, int quantity) => new(id, $"Product {id}", price, quantity);

    [Fact]
    public void Empty_cart_is_all_zero_including_shipping()
    {
        var result = _calculator.Calculate([]);

        Assert.Empty(result.Lines);
        Assert.Equal(0m, result.Subtotal);
        Assert.Equal(0m, result.Tax);
        Assert.Equal(0m, result.Shipping);
        Assert.Equal(0m, result.Total);
        Assert.Equal("USD", result.Currency);
    }

    [Fact]
    public void Single_item_below_threshold_pays_tax_and_flat_shipping()
    {
        var result = _calculator.Calculate([Line(1, 22.30m, 1)]);

        Assert.Equal(22.30m, result.Subtotal);
        Assert.Equal(1.78m, result.Tax);      // 1.784 -> 1.78
        Assert.Equal(5.99m, result.Shipping);
        Assert.Equal(30.07m, result.Total);
    }

    [Fact]
    public void Quantity_multiplies_line_total_and_can_cross_free_shipping_threshold()
    {
        var one = _calculator.Calculate([Line(2, 22.30m, 2)]);
        var three = _calculator.Calculate([Line(2, 22.30m, 3)]);

        Assert.Equal(44.60m, one.Subtotal);
        Assert.Equal(5.99m, one.Shipping);
        Assert.Equal(66.90m, three.Lines.Single().LineTotal);
        Assert.Equal(0m, three.Shipping);
        Assert.Equal(5.35m, three.Tax);       // 5.352 -> 5.35
        Assert.Equal(72.25m, three.Total);
    }

    [Fact]
    public void Multiple_products_are_summed_before_tax_is_rounded_once()
    {
        // Rounding per line would give 0.80 + 0.80 + 0.80 = 2.40; rounding the subtotal gives 2.39.
        var result = _calculator.Calculate([Line(1, 9.95m, 1), Line(2, 9.95m, 1), Line(3, 9.95m, 1)]);

        Assert.Equal(29.85m, result.Subtotal);
        Assert.Equal(2.39m, result.Tax);      // 2.388 -> 2.39
        Assert.Equal(5.99m, result.Shipping);
        Assert.Equal(38.23m, result.Total);
    }

    [Theory]
    [InlineData("49.99", "5.99")]
    [InlineData("50.00", "0")]
    [InlineData("50.01", "0")]
    [InlineData("0.01", "5.99")]
    public void Shipping_threshold_is_inclusive_and_based_on_pre_tax_subtotal(string subtotal, string expectedShipping)
    {
        var result = _calculator.Calculate([Line(1, decimal.Parse(subtotal, CultureInfo.InvariantCulture), 1)]);

        Assert.Equal(decimal.Parse(expectedShipping, CultureInfo.InvariantCulture), result.Shipping);
    }

    [Fact]
    public void Subtotal_just_below_threshold_still_pays_shipping_even_when_tax_pushes_total_above_it()
    {
        var result = _calculator.Calculate([Line(1, 49.99m, 1)]);

        Assert.Equal(4.00m, result.Tax);      // 3.9992 -> 4.00
        Assert.Equal(5.99m, result.Shipping);
        Assert.Equal(59.98m, result.Total);
    }

    [Fact]
    public void Unit_prices_and_tax_round_to_cents_half_away_from_zero()
    {
        var calculator = new PricingCalculator(TestData.PricingOptions());
        var result = calculator.Calculate([Line(1, 0.3125m, 1)]);

        Assert.Equal(0.31m, result.Subtotal); // unit price rounded first: 0.3125 -> 0.31
        Assert.Equal(0.02m, result.Tax);      // 0.0248 -> 0.02

        var midpoint = calculator.Calculate([Line(1, 0.25m, 5)]); // 1.25 * 0.08 = 0.100
        Assert.Equal(0.10m, midpoint.Tax);

        var exactHalf = new PricingCalculator(TestData.PricingOptions(o => o.TaxRate = 0.1m))
            .Calculate([Line(1, 0.05m, 1)]);  // 0.005 -> 0.01
        Assert.Equal(0.01m, exactHalf.Tax);
    }

    [Fact]
    public void Rules_follow_configuration()
    {
        var calculator = new PricingCalculator(TestData.PricingOptions(o =>
        {
            o.Currency = "ZAR";
            o.TaxRate = 0.15m;
            o.FlatShippingFee = 99m;
            o.FreeShippingThreshold = 500m;
        }));

        var result = calculator.Calculate([Line(1, 100m, 2)]);

        Assert.Equal(30m, result.Tax);
        Assert.Equal(99m, result.Shipping);
        Assert.Equal(329m, result.Total);
        Assert.Equal("ZAR", result.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_quantities_are_rejected(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _calculator.Calculate([Line(1, 10m, quantity)]));
    }

    [Fact]
    public void Minor_units_conversion_rounds_to_cents()
    {
        Assert.Equal(10995L, PricingCalculator.ToMinorUnits(109.95m));
        Assert.Equal(101L, PricingCalculator.ToMinorUnits(1.005m));
        Assert.Equal(0L, PricingCalculator.ToMinorUnits(0m));
    }
}
