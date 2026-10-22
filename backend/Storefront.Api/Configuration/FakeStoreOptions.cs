using System.ComponentModel.DataAnnotations;

namespace Storefront.Api.Configuration;

/// <summary>Settings for the upstream FakeStoreAPI integration (section "FakeStore").</summary>
public sealed class FakeStoreOptions
{
    public const string SectionName = "FakeStore";

    [Required, Url]
    public string BaseUrl { get; set; } = "https://fakestoreapi.com/";

    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>How long the normalised catalogue is cached in memory. 0 disables caching.</summary>
    [Range(0, 3600)]
    public int CatalogueCacheSeconds { get; set; } = 300;
}
