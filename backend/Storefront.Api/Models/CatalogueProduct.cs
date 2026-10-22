namespace Storefront.Api.Models;

/// <summary>A validated, normalised product held in the catalogue cache.</summary>
public sealed record CatalogueProduct(
    int Id,
    string Title,
    string Description,
    decimal Price,
    CategoryInfo Category,
    string Image,
    ProductRating? Rating,
    ProductBadge? Badge);

public sealed record ProductRating(decimal Rate, int Count);

public sealed record ProductBadge(string Code, string Label);

/// <summary>
/// A normalised category. <see cref="Group"/> is the slug of a parent grouping
/// (for example "clothing" for both men's and women's clothing), or null.
/// </summary>
public sealed record CategoryInfo(string Slug, string Name, string? Group);
