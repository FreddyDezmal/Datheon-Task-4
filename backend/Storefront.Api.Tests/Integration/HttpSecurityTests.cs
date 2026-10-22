using System.Net;
using System.Net.Http.Json;
using static Storefront.Api.Tests.Integration.AuthorizationTests;

namespace Storefront.Api.Tests.Integration;

public sealed class HttpSecurityTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        var response = await _factory.CreateClient().GetAsync("/api/store/config");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Error_responses_carry_security_headers_too()
    {
        var response = await _factory.CreateClient().GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Cors_preflight_allows_the_frontend_to_send_auth_and_idempotency_headers()
    {
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/checkout");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,authorization,idempotency-key");

        var response = await _factory.CreateClient().SendAsync(preflight);

        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        Assert.Contains("authorization", allowed);
        Assert.Contains("idempotency-key", allowed);
    }

    [Fact]
    public async Task Cors_refuses_other_origins()
    {
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/checkout");
        preflight.Headers.Add("Origin", "http://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await _factory.CreateClient().SendAsync(preflight);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Unknown_routes_return_a_404_problem()
    {
        var response = await _factory.CreateClient().GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unexpected_hosts_are_rejected()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "attacker.example";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class RateLimitTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new(accountPermitLimit: 3);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Repeated_login_attempts_are_throttled_with_a_429_problem()
    {
        var client = _factory.CreateClient();
        var credentials = new { email = "nobody@example.com", password = "Wrong!Passw0rd" };

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await client.PostAsJsonAsync("/api/account/login", credentials);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", (await ReadJson(last)).GetProperty("code").GetString());
        Assert.True(last.Headers.Contains("Retry-After"));
    }
}
