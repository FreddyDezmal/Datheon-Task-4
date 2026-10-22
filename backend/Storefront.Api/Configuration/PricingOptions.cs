using System.ComponentModel.DataAnnotations;

namespace Storefront.Api.Configuration;

/// <summary>
/// Demonstration pricing rules (section "Pricing"). These are illustrative values,
/// not a statement about any real jurisdiction's tax law.
/// All amounts assume a currency with two minor-unit digits (USD, EUR, ZAR, GBP, ...).
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>ISO 4217 currency code used for display and order totals.</summary>
    [Required, RegularExpression("^[A-Z]{3}$")]
    public string Currency { get; set; } = "USD";

    /// <summary>BCP 47 locale the frontend should use when formatting amounts.</summary>
    [Required]
    public string Locale { get; set; } = "en-US";

    /// <summary>Tax rate applied to the merchandise subtotal (0.08 = 8%). Shipping is not taxed.</summary>
    [Range(typeof(decimal), "0", "1")]
    public decimal TaxRate { get; set; } = 0.08m;

    /// <summary>Flat shipping fee charged when the merchandise subtotal is below the threshold.</summary>
    [Range(typeof(decimal), "0", "1000")]
    public decimal FlatShippingFee { get; set; } = 5.99m;

    /// <summary>Pre-tax merchandise subtotal at or above which shipping is free.</summary>
    [Range(typeof(decimal), "0", "100000")]
    public decimal FreeShippingThreshold { get; set; } = 50.00m;

    [Range(1, 999)]
    public int MaxQuantityPerLine { get; set; } = 99;

    [Range(1, 500)]
    public int MaxLineItems { get; set; } = 50;
}
