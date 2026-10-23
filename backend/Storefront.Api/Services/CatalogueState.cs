using Storefront.Api.Exceptions;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

/// <summary>
/// Process-wide state for the product catalogue (registered as a singleton):
/// <list type="bullet">
/// <item>a gate so that when the cache is empty, concurrent requests share one FakeStoreAPI call
/// instead of each making their own (which would pile load onto an upstream that may be struggling);</item>
/// <item>the last catalogue that loaded successfully, which browsing may fall back to during an outage;</item>
/// <item>the outcome of the most recent upstream call, which the readiness check reports.</item>
/// </list>
/// </summary>
public sealed class CatalogueState(TimeProvider time)
{
    public SemaphoreSlim RefreshGate { get; } = new(1, 1);

    private readonly Lock _lock = new();
    private IReadOnlyDictionary<int, CatalogueProduct>? _lastGood;
    private DateTimeOffset _lastGoodAt;
    private UpstreamFailureKind? _lastFailure;
    private DateTimeOffset? _lastFailureAt;

    public void RecordSuccess(IReadOnlyDictionary<int, CatalogueProduct> catalogue)
    {
        lock (_lock)
        {
            _lastGood = catalogue;
            _lastGoodAt = time.GetUtcNow();
            _lastFailure = null;
            _lastFailureAt = null;
        }
    }

    public void RecordFailure(UpstreamFailureKind kind)
    {
        lock (_lock)
        {
            _lastFailure = kind;
            _lastFailureAt = time.GetUtcNow();
        }
    }

    /// <summary>The last good catalogue if it is no older than <paramref name="maxAge"/>.</summary>
    public IReadOnlyDictionary<int, CatalogueProduct>? LastGoodWithin(TimeSpan maxAge)
    {
        lock (_lock)
        {
            return _lastGood is not null && maxAge > TimeSpan.Zero && time.GetUtcNow() - _lastGoodAt <= maxAge ? _lastGood : null;
        }
    }

    /// <summary>For health reporting: whether the latest upstream call failed, and how.</summary>
    public (UpstreamFailureKind? LastFailure, DateTimeOffset? Since, bool HasCatalogue) Snapshot()
    {
        lock (_lock)
        {
            return (_lastFailure, _lastFailureAt, _lastGood is not null);
        }
    }
}
