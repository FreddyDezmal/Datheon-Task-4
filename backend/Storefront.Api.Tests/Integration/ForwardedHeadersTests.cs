using System.Net;
using System.Text;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Client addresses behind a proxy, observed through the per-client sign-in rate limit (1 request per
/// window here): two requests from the same client get 429 the second time, two from different
/// clients don't.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class ForwardedHeadersTests
{
    private static StorefrontApiFactory Factory(Dictionary<string, string?>? settings = null) =>
        new(accountPermitLimit: 1, settings: settings);

    private static Dictionary<string, string?> Enabled(int hops = 1, string header = "X-Forwarded-For") => new()
    {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:TrustedProxyHops"] = hops.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ForwardedHeaders:ClientAddressHeader"] = header,
    };

    private static async Task<HttpStatusCode> SignIn(HttpClient client, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/login")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task By_default_forwarded_headers_are_ignored_so_nobody_can_pick_their_own_address()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "203.0.113.1")));
        // A different claimed address is still the same client: the limit is shared.
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "203.0.113.2")));
    }

    [Fact]
    public async Task When_enabled_each_client_gets_its_own_limit_from_the_proxy_written_entry()
    {
        using var factory = Factory(Enabled());
        var client = factory.CreateClient();

        // The proxy appends the address it saw; anything to its left came from the client.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "198.51.100.7, 203.0.113.1")));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "198.51.100.8, 203.0.113.2")));
        // Same client again, with a different forged prefix: still the same client, so limited.
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "1.1.1.1, 203.0.113.1")));
    }

    [Fact]
    public async Task Two_trusted_hops_read_the_second_entry_from_the_right()
    {
        using var factory = Factory(Enabled(hops: 2));
        var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "9.9.9.9, 203.0.113.1, 10.0.0.1")));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "9.9.9.9, 203.0.113.2, 10.0.0.1")));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignIn(client, ("X-Forwarded-For", "8.8.8.8, 203.0.113.1, 10.0.0.2")));
    }

    [Fact]
    public async Task A_different_trusted_header_can_be_used_and_x_forwarded_for_is_then_ignored()
    {
        using var factory = Factory(Enabled(header: "True-Client-IP"));
        var client = factory.CreateClient();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("True-Client-IP", "203.0.113.1"), ("X-Forwarded-For", "198.51.100.1")));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignIn(client, ("True-Client-IP", "203.0.113.2"), ("X-Forwarded-For", "198.51.100.1")));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignIn(client, ("True-Client-IP", "203.0.113.1"), ("X-Forwarded-For", "198.51.100.9")));
    }

    [Theory]
    [InlineData("0", "X-Forwarded-For", "TrustedProxyHops")]
    [InlineData("6", "X-Forwarded-For", "TrustedProxyHops")]
    [InlineData("1", "X-Forwarded-For: evil", "ClientAddressHeader")]
    [InlineData("1", "", "ClientAddressHeader")]
    public void Invalid_settings_stop_startup(string hops, string header, string named)
    {
        using var factory = Factory(new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:TrustedProxyHops"] = hops,
            ["ForwardedHeaders:ClientAddressHeader"] = header,
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains($"ForwardedHeaders:{named}", error.ToString());
    }
}
