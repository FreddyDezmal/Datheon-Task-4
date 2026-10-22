namespace Storefront.Api.DTOs;

/// <param name="Price">Unit price in major units (e.g. 109.95).</param>
/// <param name="PriceMinor">
/// Unit price in integer minor units (e.g. 10995). Clients should do arithmetic on this
/// value rather than on <paramref name="Price"/> to avoid floating-point drift.
/// </param>
public sealed record ProductDto(
    int Id,
    string Title,
    string Description,
    decimal Price,
    long PriceMinor,
    string Category,
    string CategoryName,
    string? CategoryGroup,
    string Image,
    RatingDto? Rating,
    BadgeDto? Badge);

public sealed record RatingDto(decimal Rate, int Count);

public sealed record BadgeDto(string Code, string Label);

public sealed record ProductListResponse(IReadOnlyList<ProductDto> Items, int Count);

/// <param name="ParentSlug">For a sub-category, the slug of the group it belongs to.</param>
/// <param name="IsGroup">True for a grouping entry (e.g. "clothing") that spans several categories.</param>
public sealed record CategoryDto(string Slug, string Name, int ProductCount, string? ParentSlug, bool IsGroup);
