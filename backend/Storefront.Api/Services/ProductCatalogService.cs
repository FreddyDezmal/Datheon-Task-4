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

    /// <summary>
    /// The authoritative catalogue keyed by product ID, used for checkout pricing. Never stale:
    /// if FakeStoreAPI can't be reached and the cache has expired, this throws.
    /// </summary>
    Task<IReadOnlyDictionary<int, CatalogueProduct>> GetCatalogueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True when the last browsing call on this (per-request) instance was answered from the last good
    /// catalogue because FakeStoreAPI was failing. Controllers turn it into a "Catalogue-Stale" header.
    /// </summary>
    bool ServedStale { get; }
}

/// <summary>
/// Loads products from FakeStoreAPI, normalises them once, caches the result briefly and
/// applies search, category filtering, price filtering and sorting.
/// Failed upstream calls are never cached. Browsing (list, single product, categories) may fall back to
/// the last good catalogue during an outage; checkout pricing (<see cref="GetCatalogueAsync"/>) never does.
/// </summary>
public sealed class ProductCatalogService(
    IFakeStoreClient fakeStoreClient,
    IMemoryCache cache,
    IOptions<FakeStoreOptions> options,
    CatalogueState state,
    ILogger<ProductCatalogService> logger) : IProductCatalogService
{
    internal const string CatalogueCacheKey = "catalogue:v1";

    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(options.Value.CatalogueCacheSeconds);
    private readonly TimeSpan _staleLimit = TimeSpan.FromMinutes(options.Value.StaleCatalogueMaxMinutes);

    // Image URLs come from FakeStoreAPI unchecked; plain http is accepted only when the catalogue itself
    // is configured over http (the local mock), never in production (Security-Audit.md, SA-14).
    private readonly bool _allowHttpImages = Uri.TryCreate(options.Value.BaseUrl, UriKind.Absolute, out var baseUrl) && baseUrl.Scheme == Uri.UriSchemeHttp;

    public bool ServedStale { get; private set; }

    /// <summary>Records an upstream failure for the readiness report; always true so it can sit in an exception filter.</summary>
    private bool Record(Exceptions.UpstreamServiceException ex)
    {
        state.RecordFailure(ex.Kind);
        return true;
    }

    /// <summary>The fresh catalogue, or during an upstream outage the last good one (flagged as stale).</summary>
    private async Task<IReadOnlyDictionary<int, CatalogueProduct>> GetBrowsingCatalogueAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetCatalogueAsync(cancellationToken);
        }
        catch (Exceptions.UpstreamServiceException ex) when (state.LastGoodWithin(_staleLimit) is { } lastGood)
        {
            logger.LogWarning("FakeStoreAPI unavailable ({Kind}); serving the last good catalogue to browsers.", ex.Kind);
            ServedStale = true;
            return lastGood;
        }
    }

    public async Task<ProductListResponse> GetProductsAsync(
        ProductQueryParameters query, CancellationToken cancellationToken)
    {
        if (!ProductSortParser.TryParse(query.Sort, out var sort))
        {
            throw new ArgumentException("Unsupported sort value.", nameof(query));
        }

        var catalogue = await GetBrowsingCatalogueAsync(cancellationToken);
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

        try
        {
            var product = await fakeStoreClient.GetProductAsync(id, cancellationToken);
            return product is null ? null : ToDto(Normalize(product, _allowHttpImages));
        }
        catch (Exceptions.UpstreamServiceException ex) when (Record(ex) && state.LastGoodWithin(_staleLimit) is { } lastGood)
        {
            logger.LogWarning("FakeStoreAPI unavailable ({Kind}); serving product {ProductId} from the last good catalogue.", ex.Kind, id);
            ServedStale = true;
            return lastGood.TryGetValue(id, out var stale) ? ToDto(stale) : null;
        }
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var catalogue = await GetBrowsingCatalogueAsync(cancellationToken);

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

        // One upstream call at a time: requests that arrive while it is in flight wait for it and then
        // read its result from the cache, instead of each calling FakeStoreAPI.
        await state.RefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(CatalogueCacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            IReadOnlyList<FakeStoreProduct> products;
            try
            {
                products = await fakeStoreClient.GetProductsAsync(cancellationToken);
            }
            catch (Exceptions.UpstreamServiceException ex)
            {
                state.RecordFailure(ex.Kind);
                throw;
            }

            IReadOnlyDictionary<int, CatalogueProduct> catalogue = products
                .Select(p => Normalize(p, _allowHttpImages))
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First());

            if (_cacheDuration > TimeSpan.Zero)
            {
                cache.Set(CatalogueCacheKey, catalogue, _cacheDuration);
            }

            state.RecordSuccess(catalogue);
            return catalogue;
        }
        finally
        {
            state.RefreshGate.Release();
        }
    }

    internal static CatalogueProduct Normalize(FakeStoreProduct source, bool allowHttpImages = false)
    {
        var rating = source.Rating is null ? null : new ProductRating(source.Rating.Rate, source.Rating.Count);
        var price = PricingCalculator.RoundMoney(source.Price);

        return new CatalogueProduct(
            source.Id,
            source.Title!.Trim(),
            source.Description?.Trim() ?? string.Empty,
            price,
            CategoryNormalizer.Normalize(source.Category),
            SafeImageUrl(source.Image, allowHttpImages),
            rating,
            ProductBadgeRules.Evaluate(price, rating));
    }

    /// <summary>
    /// The image URL if it's an absolute https URL (or http, when allowed) without credentials; otherwise
    /// empty, and the storefront shows its placeholder. Nothing like javascript:, data: or a relative path
    /// reaches the page.
    /// </summary>
    internal static string SafeImageUrl(string? image, bool allowHttp) =>
        Uri.TryCreate(image?.Trim(), UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || (allowHttp && url.Scheme == Uri.UriSchemeHttp))
        && string.IsNullOrEmpty(url.UserInfo)
            ? url.AbsoluteUri
            : string.Empty;

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
