using Microsoft.Extensions.Configuration;
using Storefront.Api.Configuration;

namespace Storefront.Api.Tests.Configuration;

public class CorsSettingsTests
{
    private static CorsSettings Bind(params string[] origins)
    {
        var values = origins.Select((origin, i) => new KeyValuePair<string, string?>($"Cors:AllowedOrigins:{i}", origin));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
    }

    [Fact]
    public void Configured_origins_replace_rather_than_extend_the_defaults()
    {
        // A production allowlist must not silently keep a development origin.
        var settings = Bind("https://shop.example.com");

        Assert.Equal(new[] { "https://shop.example.com" }, settings.AllowedOrigins);
    }

    [Fact]
    public void No_configuration_means_no_allowed_origins()
    {
        Assert.Empty(new CorsSettings().AllowedOrigins);
    }
}
