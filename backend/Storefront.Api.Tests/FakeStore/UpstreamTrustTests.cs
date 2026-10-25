using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.Exceptions;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.FakeStore;

/// <summary>FakeStoreAPI is outside our control: its responses are size-capped and its image URLs checked (SA-14).</summary>
[Trait("Category", TestCategories.Unit)]
public sealed class UpstreamTrustTests
{
    private static FakeStoreClient Client(StubHttpMessageHandler handler, int maxKilobytes = 16) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://fakestore.test/") },
            NullLogger<FakeStoreClient>.Instance,
            Options.Create(new FakeStoreOptions { MaxResponseKilobytes = maxKilobytes }));

    /// <summary>A body that doesn't declare its length, like a chunked response.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static string Products(int count) =>
        "[" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            $"{{\"id\":{i},\"title\":\"Product {i}\",\"price\":1.5,\"description\":\"{new string('d', 200)}\",\"category\":\"electronics\",\"image\":\"https://img.test/{i}.jpg\"}}")) + "]";

    [Fact]
    public async Task A_response_declaring_more_than_the_cap_is_refused()
    {
        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => Client(StubHttpMessageHandler.Json(Products(200))).GetProductsAsync(CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.MalformedResponse, ex.Kind);
        Assert.Contains("larger than 16 KiB", ex.Message);
    }

    [Fact]
    public async Task A_response_without_a_length_is_cut_off_at_the_cap()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnsizedContent(System.Text.Encoding.UTF8.GetBytes(Products(200))),
        }));

        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(() => Client(handler).GetProductsAsync(CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.MalformedResponse, ex.Kind);
    }

    [Fact]
    public async Task A_catalogue_within_the_cap_is_read_normally()
    {
        var products = await Client(StubHttpMessageHandler.Json(Products(20)), maxKilobytes: 2048).GetProductsAsync(CancellationToken.None);

        Assert.Equal(20, products.Count);
    }

    [Theory]
    [InlineData("https://fakestoreapi.com/img/81fPKd-2AYL._AC_SL1500_.jpg", false, "https://fakestoreapi.com/img/81fPKd-2AYL._AC_SL1500_.jpg")]
    [InlineData("  https://img.test/a.jpg ", false, "https://img.test/a.jpg")]
    [InlineData("http://img.test/a.jpg", false, "")]
    [InlineData("http://localhost:9101/img/1.svg", true, "http://localhost:9101/img/1.svg")]
    [InlineData("javascript:alert(1)", true, "")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=", true, "")]
    [InlineData("/img/relative.jpg", true, "")]
    [InlineData("https://user:pass@img.test/a.jpg", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void Only_absolute_https_image_urls_reach_the_storefront(string? image, bool allowHttp, string expected) =>
        Assert.Equal(expected, ProductCatalogService.SafeImageUrl(image, allowHttp));
}
