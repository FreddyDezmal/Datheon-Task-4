using Storefront.Api.Models;

namespace Storefront.Api.Exceptions;

public enum UpstreamFailureKind
{
    Unavailable,
    Timeout,
    MalformedResponse,
}

/// <summary>FakeStoreAPI could not be reached, timed out, or returned something unusable.</summary>
public sealed class UpstreamServiceException(UpstreamFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpstreamFailureKind Kind { get; } = kind;
}

/// <summary>The request was well-formed JSON but broke one or more business validation rules.</summary>
public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>One or more products in a checkout request do not exist in the catalogue.</summary>
public sealed class ProductsUnavailableException(IReadOnlyList<int> productIds)
    : Exception($"Products not available: {string.Join(", ", productIds)}.")
{
    public IReadOnlyList<int> ProductIds { get; } = productIds;
}

/// <summary>The total the shopper saw differs from the server-calculated total.</summary>
public sealed class PriceChangedException(decimal expectedTotal, PriceBreakdown quote)
    : Exception("Prices have changed since the cart was last updated.")
{
    public decimal ExpectedTotal { get; } = expectedTotal;
    public PriceBreakdown Quote { get; } = quote;
}

/// <summary>An Idempotency-Key was reused for a different order, or by a different caller.</summary>
public sealed class IdempotencyKeyReusedException()
    : Exception("This Idempotency-Key was already used for a different order.");
