using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Storefront.Api.Exceptions;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.FakeStore;

public class FakeStoreClientTests
{
    private const string TwoProducts = """
        [
          {"id":1,"title":"Backpack","price":109.95,"description":"d","category":"men's clothing",
           "image":"https://fakestoreapi.com/img/1.jpg","rating":{"rate":3.9,"count":120}},
          {"id":2,"title":"T-Shirt","price":22.3,"description":"d","category":"men's clothing",
           "image":"https://fakestoreapi.com/img/2.jpg","rating":{"rate":4.1,"count":259}}
        ]
        """;

    private static FakeStoreClient Client(StubHttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fakestore.test/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        }, NullLogger<FakeStoreClient>.Instance);

    [Fact]
    public async Task Parses_products_with_exact_decimal_prices()
    {
        var handler = StubHttpMessageHandler.Json(TwoProducts);

        var products = await Client(handler).GetProductsAsync(CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.Equal(109.95m, products[0].Price);
        Assert.Equal(4.1m, products[1].Rating!.Rate);
        Assert.Equal("https://fakestore.test/products", handler.Requests.Single().ToString());
    }

    [Fact]
    public async Task Skips_individually_malformed_entries()
    {
        const string body = """
            [ {"id":1,"title":"Good","price":10,"category":"electronics"},
              {"id":0,"title":"Bad id","price":10},
              {"id":3,"title":"","price":10},
              {"id":4,"title":"Negative","price":-1},
              null ]
            """;

        var products = await Client(StubHttpMessageHandler.Json(body)).GetProductsAsync(CancellationToken.None);

        Assert.Equal(1, Assert.Single(products).Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":1}")]
    [InlineData("[{\"id\":\"one\"}]")]
    public async Task Invalid_json_is_reported_as_malformed(string body)
    {
        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => Client(StubHttpMessageHandler.Json(body)).GetProductsAsync(CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.MalformedResponse, ex.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Error_status_codes_are_reported_as_unavailable(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => Client(StubHttpMessageHandler.Json("{}", status)).GetProductsAsync(CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Network_errors_are_reported_as_unavailable()
    {
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => Client(handler).GetProductsAsync(CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Timeouts_are_reported_as_timeout()
    {
        var client = Client(StubHttpMessageHandler.Hangs(), TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => client.GetProductsAsync(CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_disguised_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => Client(StubHttpMessageHandler.Hangs()).GetProductsAsync(cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task Unknown_single_product_returns_null(string body)
    {
        // FakeStoreAPI answers /products/{unknown} with 200 and an empty body.
        var product = await Client(StubHttpMessageHandler.Json(body)).GetProductAsync(999, CancellationToken.None);

        Assert.Null(product);
    }

    [Fact]
    public async Task Not_found_single_product_returns_null()
    {
        var handler = StubHttpMessageHandler.Json("", HttpStatusCode.NotFound);

        Assert.Null(await Client(handler).GetProductAsync(999, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_single_product_throws()
    {
        var handler = StubHttpMessageHandler.Json("""{"id":7,"title":"","price":1}""");

        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => Client(handler).GetProductAsync(7, CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.MalformedResponse, ex.Kind);
    }
}
