namespace Storefront.Api.DTOs;

/// <summary>
/// Public pricing rules so the frontend can show estimates using exactly the same rules
/// the server applies at checkout. Minor-unit fields are integers (cents for USD).
/// </summary>
public sealed record StoreConfigDto(
    string Currency,
    string Locale,
    decimal TaxRate,
    int TaxRateBasisPoints,
    decimal FlatShippingFee,
    long FlatShippingFeeMinor,
    decimal FreeShippingThreshold,
    long FreeShippingThresholdMinor,
    int MaxQuantityPerLine,
    int MaxLineItems);
