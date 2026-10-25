using System.Net;
using System.Net.Http.Json;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>Responses with tokens or personal data are never cached (Security-Audit.md, SA-09).</summary>
[Trait("Category", TestCategories.Api)]
public sealed class CacheControlTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.True(response.Headers.CacheControl?.NoStore == true, $"{response.RequestMessage?.RequestUri} lacks Cache-Control: no-store");

    [Fact]
    public async Task Tokens_account_info_orders_and_confirmations_are_no_store()
    {
        var email = $"cache-{Guid.NewGuid():N}@example.com";
        var anonymous = _factory.CreateClient();
        var register = await anonymous.PostAsJsonAsync("/api/account/register", new { email, password = "Str0ng!Passw0rd" });
        var login = await anonymous.PostAsJsonAsync("/api/account/login", new { email, password = "Str0ng!Passw0rd" });
        var tokens = await login.Content.ReadFromJsonAsync<StorefrontApiFactory.TokenResponse>();
        var refresh = await anonymous.PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens!.RefreshToken });
        var (client, _) = await _factory.SignInAsync(email);

        var info = await client.GetAsync("/api/account/manage/info");
        var orders = await client.GetAsync("/api/orders");
        var missing = await client.GetAsync("/api/orders/DEMO-20260101-AAAAAA");
        var checkout = await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));
        var failedLogin = await anonymous.PostAsJsonAsync("/api/account/login", new { email, password = "Wr0ng!Password" });

        foreach (var response in new[] { register, login, refresh, info, orders, missing, checkout, failedLogin })
        {
            AssertNoStore(response);
        }

        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
    }

    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/categories")]
    [InlineData("/api/store/config")]
    public async Task Public_catalogue_responses_are_not_marked_no_store(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(true, response.Headers.CacheControl?.NoStore);
    }
}
