using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Storefront.Api.DTOs;
using Storefront.Api.Exceptions;
using Storefront.Api.Health;
using Storefront.Api.Models;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Catalogue;

[Trait("Category", TestCategories.Unit)]
public sealed class CatalogueResilienceTests
{
    private readonly InMemoryFakeStoreClient _client = new(TestData.Catalogue());
    private static UpstreamServiceException Down() => new(UpstreamFailureKind.Unavailable, "down");

    [Fact]
    public async Task Browsing_falls_back_to_the_last_good_catalogue_and_says_so()
    {
        var state = new CatalogueState(TimeProvider.System);
        await TestData.CatalogService(_client, cacheSeconds: 0, state: state).GetProductsAsync(new ProductQueryParameters(), CancellationToken.None);
        _client.FailWith = Down();

        var service = TestData.CatalogService(_client, cacheSeconds: 0, state: state);
        var products = await service.GetProductsAsync(new ProductQueryParameters(), CancellationToken.None);

        Assert.NotEmpty(products.Items);
        Assert.True(service.ServedStale);
    }

    [Fact]
    public async Task Checkout_pricing_never_uses_the_stale_copy()
    {
        var state = new CatalogueState(TimeProvider.System);
        await TestData.CatalogService(_client, cacheSeconds: 0, state: state).GetCatalogueAsync(CancellationToken.None);
        _client.FailWith = Down();

        var service = TestData.CatalogService(_client, cacheSeconds: 0, state: state);

        await Assert.ThrowsAsync<UpstreamServiceException>(() => service.GetCatalogueAsync(CancellationToken.None));
        Assert.False(service.ServedStale);
    }

    [Fact]
    public async Task A_copy_older_than_the_stale_limit_is_not_served()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var state = new CatalogueState(clock);
        await TestData.CatalogService(_client, cacheSeconds: 0, state: state, staleMinutes: 60).GetCatalogueAsync(CancellationToken.None);
        _client.FailWith = Down();
        clock.Advance(TimeSpan.FromMinutes(61));

        var service = TestData.CatalogService(_client, cacheSeconds: 0, state: state, staleMinutes: 60);

        await Assert.ThrowsAsync<UpstreamServiceException>(() => service.GetProductsAsync(new ProductQueryParameters(), CancellationToken.None));
    }

    [Fact]
    public async Task Setting_the_stale_limit_to_zero_turns_the_fallback_off()
    {
        var state = new CatalogueState(TimeProvider.System);
        await TestData.CatalogService(_client, cacheSeconds: 0, state: state, staleMinutes: 0).GetCatalogueAsync(CancellationToken.None);
        _client.FailWith = Down();

        await Assert.ThrowsAsync<UpstreamServiceException>(() =>
            TestData.CatalogService(_client, cacheSeconds: 0, state: state, staleMinutes: 0).GetCategoriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_single_product_is_served_from_the_last_good_copy_during_an_outage()
    {
        var state = new CatalogueState(TimeProvider.System);
        await TestData.CatalogService(_client, cacheSeconds: 0, state: state).GetCatalogueAsync(CancellationToken.None);
        _client.FailWith = Down();
        var service = TestData.CatalogService(_client, cacheSeconds: 0, state: state);

        var product = await service.GetProductAsync(2, CancellationToken.None);
        var missing = await service.GetProductAsync(999, CancellationToken.None);

        Assert.Equal(2, product?.Id);
        Assert.Null(missing);
        Assert.True(service.ServedStale);
    }

    /// <summary>An upstream that answers only when the test lets it, counting calls.</summary>
    private sealed class GatedClient(List<FakeStoreProduct> products) : IFakeStoreClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListCalls;

        public async Task<IReadOnlyList<FakeStoreProduct>> GetProductsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ListCalls);
            await Release.Task;
            return products;
        }

        public Task<FakeStoreProduct?> GetProductAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Concurrent_requests_with_an_empty_cache_share_one_upstream_call()
    {
        var upstream = new GatedClient(TestData.Catalogue());
        var state = new CatalogueState(TimeProvider.System);
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Ten requests, each with its own service instance (as each HTTP request gets), sharing the
        // process-wide cache and state.
        var requests = Enumerable.Range(0, 10)
            .Select(_ => TestData.CatalogService(upstream, cacheSeconds: 300, state: state, cache: cache).GetCatalogueAsync(CancellationToken.None))
            .ToList();
        await Task.Delay(50);
        upstream.Release.SetResult();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, upstream.ListCalls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task The_catalogue_health_check_is_degraded_after_a_failure_and_healthy_after_recovery()
    {
        var state = new CatalogueState(TimeProvider.System);
        var check = new CatalogueHealthCheck(state);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        state.RecordFailure(UpstreamFailureKind.Timeout);
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext())).Status); // never Unhealthy

        state.RecordSuccess(new Dictionary<int, CatalogueProduct>());
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    [Fact]
    public async Task Readiness_turns_unhealthy_as_soon_as_shutdown_begins()
    {
        var lifetime = new FakeLifetime();
        var check = new ShutdownHealthCheck(lifetime);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        lifetime.StopApplication();

        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
