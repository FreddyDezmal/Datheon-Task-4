using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storefront.Api.Data;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Token lifetimes and malformed credentials, through the real authentication pipeline.
/// A test clock replaces the system clock, so expiry is real rather than simulated with a forged token.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class TokenBoundaryTests : IDisposable
{
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly StorefrontApiFactory _factory;

    public TokenBoundaryTests() => _factory = new StorefrontApiFactory(timeProvider: _clock);

    public void Dispose() => _factory.Dispose();

    private async Task<int> OrderCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StorefrontDbContext>().Orders.CountAsync();
    }

    [Fact]
    public async Task An_expired_access_token_is_refused_by_protected_endpoints()
    {
        var (client, tokens) = await _factory.SignInAsync("expiry@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders")).StatusCode);

        _clock.Advance(TimeSpan.FromSeconds(tokens.ExpiresIn + 1));

        var response = await client.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", (await AuthorizationTests.ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_expired_access_token_cannot_place_a_guest_order_instead()
    {
        var (client, tokens) = await _factory.SignInAsync("expired-checkout@example.com");
        _clock.Advance(TimeSpan.FromSeconds(tokens.ExpiresIn + 1));

        var response = await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await OrderCountAsync());
    }

    [Fact]
    public async Task A_token_is_still_accepted_just_before_it_expires()
    {
        var (client, tokens) = await _factory.SignInAsync("boundary@example.com");
        _clock.Advance(TimeSpan.FromSeconds(tokens.ExpiresIn - 5));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task The_refresh_token_issues_a_working_access_token_after_the_old_one_expires()
    {
        var (client, tokens) = await _factory.SignInAsync("refresh@example.com");
        var placed = await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));
        placed.EnsureSuccessStatusCode();
        _clock.Advance(TimeSpan.FromHours(2));

        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.Content.ReadFromJsonAsync<StorefrontApiFactory.TokenResponse>();

        var fresh = _factory.CreateClient();
        fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed!.AccessToken);
        var orders = await AuthorizationTests.ReadJson(await fresh.GetAsync("/api/orders"));
        Assert.Equal(1, orders.GetArrayLength());
    }

    [Fact]
    public async Task An_expired_refresh_token_is_rejected()
    {
        var (_, tokens) = await _factory.SignInAsync("old-refresh@example.com");
        _clock.Advance(TimeSpan.FromDays(15)); // Identity's default refresh-token lifetime is 14 days

        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task An_access_token_cannot_be_used_as_a_refresh_token()
    {
        var (_, tokens) = await _factory.SignInAsync("swap@example.com");

        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.AccessToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Theory]
    [InlineData("Basic", "YWRhOnBhc3N3b3Jk")]
    [InlineData("Bearer", "not-a-real-token")]
    [InlineData("Token", "abc")]
    public async Task Checkout_with_an_unusable_authorization_header_is_refused_and_saves_nothing(string scheme, string value)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, value);

        var response = await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await OrderCountAsync());
    }

    [Fact]
    public async Task A_refresh_token_cannot_be_used_as_an_access_token()
    {
        var (_, tokens) = await _factory.SignInAsync("misuse@example.com");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orders")).StatusCode);
    }
}
