using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Storefront.Api.Tests.Integration;

public sealed class AuthorizationTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    internal static object Order(params (int Id, int Qty)[] items) => new
    {
        customer = new { name = "Ada Lovelace", email = "ada@example.com" },
        items = items.Select(i => new { productId = i.Id, quantity = i.Qty }),
        expectedTotal = 30.07m, // correct for the default single line (1 x product 2)
    };

    internal static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/products")]
    [InlineData("/api/products/2")]
    [InlineData("/api/categories")]
    [InlineData("/api/store/config")]
    public async Task Catalogue_endpoints_are_public(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Guest_checkout_is_still_allowed()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/checkout", Order((2, 1)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/orders")]
    [InlineData("/api/orders/DEMO-20261022-AAAAAA")]
    public async Task Order_endpoints_reject_anonymous_requests_with_401_problem_details(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("UNAUTHORIZED", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_invalid_bearer_token_is_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_in_checkout_appears_in_my_orders()
    {
        var ada = await _factory.CreateSignedInClientAsync("ada@example.com");

        var checkout = await ada.PostAsJsonAsync("/api/checkout", Order((2, 1)));
        var reference = (await ReadJson(checkout)).GetProperty("orderReference").GetString();

        var list = await ReadJson(await ada.GetAsync("/api/orders"));
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(reference, row.GetProperty("orderReference").GetString());
        Assert.Equal(30.07m, row.GetProperty("total").GetDecimal());

        var detail = await ada.GetAsync($"/api/orders/{reference}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var line = Assert.Single((await ReadJson(detail)).GetProperty("lines").EnumerateArray());
        Assert.Equal(22.30m, line.GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task Customers_cannot_see_each_others_orders()
    {
        var ada = await _factory.CreateSignedInClientAsync("ada@example.com");
        var bob = await _factory.CreateSignedInClientAsync("bob@example.com");
        var reference = (await ReadJson(await ada.PostAsJsonAsync("/api/checkout", Order((2, 1)))))
            .GetProperty("orderReference").GetString();

        var bobsView = await bob.GetAsync($"/api/orders/{reference}");
        var bobsList = await ReadJson(await bob.GetAsync("/api/orders"));

        Assert.Equal(HttpStatusCode.NotFound, bobsView.StatusCode);
        Assert.Empty(bobsList.EnumerateArray());
    }

    [Fact]
    public async Task Guest_orders_are_not_attached_to_any_account()
    {
        await _factory.CreateClient().PostAsJsonAsync("/api/checkout", Order((2, 1)));
        var ada = await _factory.CreateSignedInClientAsync("ada@example.com");

        Assert.Empty((await ReadJson(await ada.GetAsync("/api/orders"))).EnumerateArray());
    }

    [Fact]
    public async Task A_user_id_in_the_request_body_is_ignored()
    {
        var ada = await _factory.CreateSignedInClientAsync("ada@example.com");
        var bob = await _factory.CreateSignedInClientAsync("bob@example.com");
        // Bob tries to place an order "as" someone else by adding ownership fields to the body.
        var body = new
        {
            customer = new { name = "Bob", email = "bob@example.com" },
            items = new[] { new { productId = 2, quantity = 1 } },
            expectedTotal = 30.07m,
            userId = "someone-else",
            unitPrice = 0.01m,
        };
        var response = await bob.PostAsJsonAsync("/api/checkout", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(30.07m, (await ReadJson(response)).GetProperty("totals").GetProperty("total").GetDecimal());
        Assert.Single((await ReadJson(await bob.GetAsync("/api/orders"))).EnumerateArray());
        Assert.Empty((await ReadJson(await ada.GetAsync("/api/orders"))).EnumerateArray());
    }

    [Fact]
    public async Task Weak_passwords_are_rejected_as_validation_problems()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/account/register", new { email = "weak@example.com", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").EnumerateObject().Any());
    }

    [Fact]
    public async Task Wrong_password_returns_401_and_no_token()
    {
        await _factory.CreateSignedInClientAsync("ada@example.com");

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/account/login", new { email = "ada@example.com", password = "Wrong!Passw0rd" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Passwords_are_stored_hashed()
    {
        await _factory.CreateSignedInClientAsync("ada@example.com", "Str0ng!Passw0rd");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.StorefrontDbContext>();
        var user = db.Users.Single(u => u.Email == "ada@example.com");
        Assert.NotNull(user.PasswordHash);
        Assert.DoesNotContain("Str0ng!Passw0rd", user.PasswordHash);
    }
}
