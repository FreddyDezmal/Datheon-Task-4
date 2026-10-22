using System.Net;
using System.Text.Json;
using Storefront.Api.Exceptions;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

public interface IFakeStoreClient
{
    /// <summary>Returns every valid product. Malformed individual entries are skipped and logged.</summary>
    Task<IReadOnlyList<FakeStoreProduct>> GetProductsAsync(CancellationToken cancellationToken);

    /// <summary>Returns the product, or null when FakeStoreAPI has no product with that ID.</summary>
    Task<FakeStoreProduct?> GetProductAsync(int id, CancellationToken cancellationToken);
}

/// <summary>Typed HttpClient for FakeStoreAPI, registered through IHttpClientFactory.</summary>
public sealed class FakeStoreClient(HttpClient httpClient, ILogger<FakeStoreClient> logger) : IFakeStoreClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FakeStoreProduct>> GetProductsAsync(CancellationToken cancellationToken)
    {
        var products = await GetJsonAsync<List<FakeStoreProduct?>>("products", cancellationToken)
            ?? throw new UpstreamServiceException(
                UpstreamFailureKind.MalformedResponse, "FakeStoreAPI returned an empty product list response.");

        var valid = new List<FakeStoreProduct>(products.Count);
        foreach (var product in products)
        {
            if (IsValid(product))
            {
                valid.Add(product!);
            }
            else
            {
                logger.LogWarning("Skipping malformed product entry from FakeStoreAPI (id: {ProductId}).", product?.Id);
            }
        }

        return valid;
    }

    public async Task<FakeStoreProduct?> GetProductAsync(int id, CancellationToken cancellationToken)
    {
        // FakeStoreAPI answers unknown IDs with 200 and an empty body (or "null"); both become null here.
        var product = await GetJsonAsync<FakeStoreProduct>($"products/{id}", cancellationToken);
        if (product is null)
        {
            return null;
        }

        if (!IsValid(product))
        {
            throw new UpstreamServiceException(
                UpstreamFailureKind.MalformedResponse, $"FakeStoreAPI returned a malformed product for id {id}.");
        }

        return product;
    }

    internal static bool IsValid(FakeStoreProduct? product) =>
        product is not null
        && product.Id > 0
        && !string.IsNullOrWhiteSpace(product.Title)
        && product.Price >= 0
        && (product.Rating is null || (product.Rating.Rate is >= 0 and <= 5 && product.Rating.Count >= 0));

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await httpClient.GetAsync(relativeUrl, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpstreamServiceException(
                    UpstreamFailureKind.Unavailable,
                    $"FakeStoreAPI responded with {(int)response.StatusCode} for '{relativeUrl}'.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body) || body.Trim() == "null")
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a cancellation that the caller did not request.
            throw new UpstreamServiceException(
                UpstreamFailureKind.Timeout, $"FakeStoreAPI did not respond in time for '{relativeUrl}'.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpstreamServiceException(
                UpstreamFailureKind.Unavailable, $"Could not reach FakeStoreAPI for '{relativeUrl}'.", ex);
        }
        catch (JsonException ex)
        {
            throw new UpstreamServiceException(
                UpstreamFailureKind.MalformedResponse, $"FakeStoreAPI returned invalid JSON for '{relativeUrl}'.", ex);
        }
    }
}
