using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Storefront.Api.Data.Configurations;
using Storefront.Api.DTOs;

namespace Storefront.Api.Services;

/// <summary>
/// Who is checking out, as established by the HTTP layer from the authentication token and
/// request headers. Never populated from the request body.
/// </summary>
/// <param name="UserId">The signed-in user's ID, or null for a guest checkout.</param>
/// <param name="IdempotencyKey">The Idempotency-Key header value, if the client sent one.</param>
public sealed record CheckoutCaller(string? UserId, string? IdempotencyKey)
{
    public static CheckoutCaller Guest { get; } = new(null, null);
}

/// <param name="IsReplay">True when the order already existed for this Idempotency-Key and was returned again.</param>
public sealed record CheckoutResult(CheckoutConfirmationDto Confirmation, bool IsReplay);

public static partial class IdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    // Opaque client-chosen token: UUIDs and similar. Restricted so it is safe to log and index.
    [GeneratedRegex("^[A-Za-z0-9_-]{8,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string key) =>
        key.Length <= OrderConfiguration.IdempotencyKeyMaxLength && Pattern().IsMatch(key);

    /// <summary>
    /// A stable hash of what was ordered and for whom. Prices and the expected total are left out on
    /// purpose: they are server-calculated, and a replay must return the order as originally priced.
    /// </summary>
    public static string Fingerprint(CheckoutRequestDto request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Customer?.Name?.Trim()).Append('\n');
        builder.Append(request.Customer?.Email?.Trim().ToLowerInvariant()).Append('\n');
        foreach (var item in (request.Items ?? []).OrderBy(i => i.ProductId))
        {
            builder.Append(item.ProductId).Append(':').Append(item.Quantity).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
