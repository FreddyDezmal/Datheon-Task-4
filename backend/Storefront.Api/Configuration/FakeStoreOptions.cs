using System.ComponentModel.DataAnnotations;

namespace Storefront.Api.Configuration;

/// <summary>Settings for the upstream FakeStoreAPI integration (section "FakeStore").</summary>
public sealed class FakeStoreOptions : IValidatableObject
{
    public const string SectionName = "FakeStore";

    [Required, Url]
    public string BaseUrl { get; set; } = "https://fakestoreapi.com/";

    /// <summary>
    /// Total time budget for one upstream call, retries included. Kept below the frontend's own
    /// 15-second timeout so the shopper gets the API's explanation rather than a browser timeout.
    /// </summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Time allowed for a single attempt before it is abandoned and (if budget remains) retried.</summary>
    [Range(1, 60)]
    public int AttemptTimeoutSeconds { get; set; } = 4;

    /// <summary>Extra attempts after a transient failure (network error, timeout, 408/429/5xx). GET only.</summary>
    [Range(0, 5)]
    public int RetryCount { get; set; } = 2;

    /// <summary>First retry delay; each later retry doubles it (with jitter), capped at 2 seconds.</summary>
    [Range(0, 5000)]
    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>How long the normalised catalogue is cached in memory. 0 disables caching.</summary>
    [Range(0, 3600)]
    public int CatalogueCacheSeconds { get; set; } = 300;

    /// <summary>
    /// While FakeStoreAPI is failing, browsing may show the last catalogue that loaded successfully,
    /// if it is at most this old (flagged to the client as stale). Checkout never uses it. 0 disables.
    /// </summary>
    [Range(0, 1440)]
    public int StaleCatalogueMaxMinutes { get; set; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AttemptTimeoutSeconds > TimeoutSeconds)
        {
            yield return new ValidationResult(
                "FakeStore:AttemptTimeoutSeconds must not be greater than FakeStore:TimeoutSeconds.",
                [nameof(AttemptTimeoutSeconds), nameof(TimeoutSeconds)]);
        }
    }
}
