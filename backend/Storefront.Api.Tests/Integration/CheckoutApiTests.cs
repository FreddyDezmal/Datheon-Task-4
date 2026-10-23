using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Storefront.Api.Data;
using Storefront.Api.Tests.TestSupport;
using static Storefront.Api.Tests.Integration.AuthorizationTests;

namespace Storefront.Api.Tests.Integration;

[Trait("Category", TestCategories.Api)]
public sealed class CheckoutApiTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage CheckoutRequest(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private int CountOrders()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<StorefrontDbContext>().Orders.Count();
    }

    [Fact]
    public async Task Checkout_response_contract_is_unchanged()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/checkout", Order((2, 1)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        foreach (var field in new[] { "orderReference", "createdAt", "customerName", "customerEmail", "lines", "totals", "isSimulated", "notice" })
        {
            Assert.True(body.TryGetProperty(field, out _), $"missing '{field}'");
        }

        Assert.True(body.GetProperty("isSimulated").GetBoolean());
        Assert.StartsWith("DEMO-", body.GetProperty("orderReference").GetString());
        Assert.Equal(1, CountOrders());
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_returns_the_same_order()
    {
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(CheckoutRequest(Order((2, 1)), key));
        var retry = await client.SendAsync(CheckoutRequest(Order((2, 1)), key));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(
            (await ReadJson(first)).GetProperty("orderReference").GetString(),
            (await ReadJson(retry)).GetProperty("orderReference").GetString());
        Assert.Equal(1, CountOrders());
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_order_returns_422()
    {
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        await client.SendAsync(CheckoutRequest(Order((2, 1)), key));

        var different = new
        {
            customer = new { name = "Ada Lovelace", email = "ada@example.com" },
            items = new[] { new { productId = 2, quantity = 2 } },
            expectedTotal = 54.16m,
        };
        var response = await client.SendAsync(CheckoutRequest(different, key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal(1, CountOrders());
    }

    [Fact]
    public async Task A_malformed_idempotency_key_is_a_validation_error()
    {
        var response = await _factory.CreateClient().SendAsync(CheckoutRequest(Order((2, 1)), "bad key!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadJson(response)).GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task A_stale_total_still_returns_409_with_a_quote_and_saves_nothing()
    {
        var stale = new
        {
            customer = new { name = "Ada Lovelace", email = "ada@example.com" },
            items = new[] { new { productId = 2, quantity = 1 } },
            expectedTotal = 1.00m,
        };
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/checkout", stale);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("PRICE_CHANGED", problem.GetProperty("code").GetString());
        Assert.Equal(30.07m, problem.GetProperty("quote").GetProperty("totals").GetProperty("total").GetDecimal());
        Assert.Equal(0, CountOrders());
    }

    [Fact]
    public async Task Orders_survive_an_api_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storefront-restart-{Guid.NewGuid():N}.db");
        try
        {
            string? reference;
            await using (var firstRun = new StorefrontApiFactory(path))
            {
                var ada = await firstRun.CreateSignedInClientAsync("ada@example.com");
                reference = (await ReadJson(await ada.PostAsJsonAsync("/api/checkout", Order((2, 1)))))
                    .GetProperty("orderReference").GetString();
            }

            SqliteConnection.ClearAllPools();

            await using var secondRun = new StorefrontApiFactory(path);
            var sameUser = await secondRun.CreateSignedInClientAsync("ada@example.com");
            var detail = await sameUser.GetAsync($"/api/orders/{reference}");

            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Equal(30.07m, (await ReadJson(detail)).GetProperty("totals").GetProperty("total").GetDecimal());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestDatabase.TryDelete(path);
        }
    }

    [Fact]
    public async Task Malformed_json_returns_a_400_problem()
    {
        var response = await _factory.CreateClient().PostAsync(
            "/api/checkout", new StringContent("{\"items\": [", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_expected_total_is_still_rejected()
    {
        var body = new
        {
            customer = new { name = "Ada Lovelace", email = "ada@example.com" },
            items = new[] { new { productId = 2, quantity = 1 } },
        };
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/checkout", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadJson(response)).GetProperty("errors").TryGetProperty("expectedTotal", out _));
    }
}
