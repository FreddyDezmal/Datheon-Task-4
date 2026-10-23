using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.Data;
using Storefront.Api.Models;
using Storefront.Api.Services;

namespace Storefront.Api.Tests.TestSupport;

internal static class TestData
{
    public static FakeStoreProduct Product(
        int id, string title, decimal price, string category, decimal rate = 4.0m, int count = 50) => new()
    {
        Id = id,
        Title = title,
        Price = price,
        Category = category,
        Description = $"{title} description",
        Image = $"https://example.test/{id}.jpg",
        Rating = new FakeStoreRating { Rate = rate, Count = count },
    };

    /// <summary>A small catalogue covering every FakeStoreAPI category spelling.</summary>
    public static List<FakeStoreProduct> Catalogue() =>
    [
        Product(1, "Fjallraven Backpack", 109.95m, "men's clothing", 3.9m, 120),
        Product(2, "Mens Casual Slim Fit T-Shirt", 22.30m, "men's clothing", 4.1m, 259),
        Product(5, "Gold Dragon Bracelet", 695.00m, "jewelery", 4.6m, 400),
        Product(9, "WD 2TB Portable Hard Drive", 64.00m, "electronics", 3.3m, 203),
        Product(14, "Samsung 49-Inch Curved Monitor", 999.99m, "electronics", 2.2m, 140),
        Product(18, "Women's Short Sleeve Boat Neck", 9.85m, "women's clothing", 4.7m, 130),
        Product(19, "Women's Short Sleeve Moisture Tee", 7.95m, "women's clothing", 4.5m, 146),
    ];

    public static IOptions<PricingOptions> PricingOptions(Action<PricingOptions>? configure = null)
    {
        var options = new PricingOptions();
        configure?.Invoke(options);
        return Options.Create(options);
    }

    public static ProductCatalogService CatalogService(
        IFakeStoreClient client,
        int cacheSeconds = 300,
        CatalogueState? state = null,
        IMemoryCache? cache = null,
        int staleMinutes = 60) => new(
        client,
        cache ?? new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new FakeStoreOptions { CatalogueCacheSeconds = cacheSeconds, StaleCatalogueMaxMinutes = staleMinutes }),
        state ?? new CatalogueState(TimeProvider.System),
        NullLogger<ProductCatalogService>.Instance);

    public static readonly DateTimeOffset Now = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    public static CheckoutService CheckoutService(
        IFakeStoreClient client, StorefrontDbContext db, TimeProvider? time = null, IOrderReferenceGenerator? references = null)
    {
        var pricing = PricingOptions();
        time ??= new FixedTimeProvider(Now);
        return new CheckoutService(
            new CheckoutRequestValidator(pricing),
            CatalogService(client),
            new PricingCalculator(pricing),
            references ?? new OrderReferenceGenerator(time),
            db,
            time,
            NullLogger<CheckoutService>.Instance);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>In-memory IFakeStoreClient that can be told to fail and counts its calls.</summary>
internal sealed class InMemoryFakeStoreClient(List<FakeStoreProduct> products) : IFakeStoreClient
{
    public int ListCalls { get; private set; }
    public int SingleCalls { get; private set; }
    public Exception? FailWith { get; set; }

    public Task<IReadOnlyList<FakeStoreProduct>> GetProductsAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult<IReadOnlyList<FakeStoreProduct>>(products);
    }

    public Task<FakeStoreProduct?> GetProductAsync(int id, CancellationToken cancellationToken)
    {
        SingleCalls++;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult(products.FirstOrDefault(p => p.Id == id));
    }
}
