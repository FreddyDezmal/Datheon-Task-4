using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.DTOs;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

public interface IProductCatalogService
{
    Task<ProductListResponse> GetProductsAsync(ProductQueryParameters query, CancellationToken cancellationToken);

    Task<ProductDto?> GetProductAsync(int id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken);

    /// <summary>The authoritative catalogue keyed by product ID, used for checkout pricing.</summary>
    Task<IReadOnlyDictionary<int, CatalogueProduct>> GetCatalogueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Loads products from FakeStoreAPI, normalises them once, caches the result briefly and
/// applies search, category filtering, price filtering and sorting.
/// Failed upstream calls are never cached.
/// </summary>
public sealed class ProductCatalogService(
    IFakeStoreClient fakeStoreClient,
    IMemoryCache cache,
    IOptions<FakeStoreOptions> options) : IProductCatalogService
{
    internal const string CatalogueCacheKey = "catalogue:v1";

    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(options.Value.CatalogueCacheSeconds);

    public async Task<ProductListResponse> GetProductsAsync(
        ProductQueryParameters query, CancellationToken cancellationToken)
    {
        if (!ProductSortParser.TryParse(query.Sort, out var sort))
        {
            throw new ArgumentException("Unsupported sort value.", nameof(query));
        }

        var catalogue = await GetCatalogueAsync(cancellationToken);
        var terms = SplitSearchTerms(query.Search);

        IEnumerable<CatalogueProduct> results = catalogue.Values
            .Where(p => CategoryNormalizer.Matches(p.Category, query.Category))
            .Where(p => terms.All(term => p.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Where(p => query.MinPrice is not { } min || p.Price >= min)
            .Where(p => query.MaxPrice is not { } max || p.Price <= max);

        results = sort switch
        {
            ProductSort.PriceAscending => results.OrderBy(p => p.Price).ThenBy(p => p.Id),
            ProductSort.PriceDescending => results.OrderByDescending(p => p.Price).ThenBy(p => p.Id),
            ProductSort.Rating => results
                .OrderByDescending(p => p.Rating?.Rate ?? -1)
                .ThenByDescending(p => p.Rating?.Count ?? 0)
                .ThenBy(p => p.Id),
            _ => results.OrderBy(p => p.Id),
        };

        var items = results.Select(ToDto).ToList();
        return new ProductListResponse(items, items.Count);
    }

    public async Task<ProductDto?> GetProductAsync(int id, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CatalogueCacheKey, out IReadOnlyDictionary<int, CatalogueProduct>? cached) && cached is not null)
        {
            return cached.TryGetValue(id, out var hit) ? ToDto(hit) : null;
        }

        var product = await fakeStoreClient.GetProductAsync(id, cancellationToken);
        return product is null ? null : ToDto(Normalize(product));
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var catalogue = await GetCatalogueAsync(cancellationToken);

        var categories = catalogue.Values
            .GroupBy(p => p.Category)
            .Select(g => new CategoryDto(g.Key.Slug, g.Key.Name, g.Count(), g.Key.Group, IsGroup: false))
            .ToList();

        var groups = categories
            .Where(c => c.ParentSlug is not null)
            .GroupBy(c => c.ParentSlug!)
            .Select(g => new CategoryDto(g.Key, GroupName(g.Key), g.Sum(c => c.ProductCount), null, IsGroup: true));

        // Order: standalone categories, then each group followed by its children.
        var ordered = new List<CategoryDto>();
        ordered.AddRange(categories.Where(c => c.ParentSlug is null).OrderBy(c => c.Name, StringComparer.Ordinal));
        foreach (var group in groups.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            ordered.Add(group);
            ordered.AddRange(categories.Where(c => c.ParentSlug == group.Slug).OrderBy(c => c.Name, StringComparer.Ordinal));
        }

        return ordered;
    }

    public async Task<IReadOnlyDictionary<int, CatalogueProduct>> GetCatalogueAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CatalogueCacheKey, out IReadOnlyDictionary<int, CatalogueProduct>? cached) && cached is not null)
        {
            return cached;
        }

        var products = await fakeStoreClient.GetProductsAsync(cancellationToken);
        var catalogue = products
            .Select(Normalize)
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.First());

        if (_cacheDuration > TimeSpan.Zero)
        {
            cache.Set(CatalogueCacheKey, (IReadOnlyDictionary<int, CatalogueProduct>)catalogue, _cacheDuration);
        }

        return catalogue;
    }

    internal static CatalogueProduct Normalize(FakeStoreProduct source)
    {
        var rating = source.Rating is null ? null : new ProductRating(source.Rating.Rate, source.Rating.Count);
        var price = PricingCalculator.RoundMoney(source.Price);

        return new CatalogueProduct(
            source.Id,
            source.Title!.Trim(),
            source.Description?.Trim() ?? string.Empty,
            price,
            CategoryNormalizer.Normalize(source.Category),
            source.Image ?? string.Empty,
            rating,
            ProductBadgeRules.Evaluate(price, rating));
    }

    internal static ProductDto ToDto(CatalogueProduct p) => new(
        p.Id,
        p.Title,
        p.Description,
        p.Price,
        PricingCalculator.ToMinorUnits(p.Price),
        p.Category.Slug,
        p.Category.Name,
        p.Category.Group,
        p.Image,
        p.Rating is null ? null : new RatingDto(p.Rating.Rate, p.Rating.Count),
        p.Badge is null ? null : new BadgeDto(p.Badge.Code, p.Badge.Label));

    private static string[] SplitSearchTerms(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? []
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string GroupName(string slug) =>
        slug == CategoryNormalizer.ClothingGroupSlug
            ? CategoryNormalizer.ClothingGroupName
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('-', ' '));
}
