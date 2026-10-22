using System.Security.Cryptography;

namespace Storefront.Api.Services;

public interface IOrderReferenceGenerator
{
    string Next();
}

/// <summary>Generates demo references such as "DEMO-20261022-7KQ2XM".</summary>
public sealed class OrderReferenceGenerator(TimeProvider timeProvider) : IOrderReferenceGenerator
{
    // No 0/O or 1/I to keep references easy to read aloud.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string Next()
    {
        var date = timeProvider.GetUtcNow().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = RandomNumberGenerator.GetString(Alphabet, 6);
        return $"DEMO-{date}-{suffix}";
    }
}
