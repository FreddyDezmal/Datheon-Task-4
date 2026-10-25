using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Only the account endpoints the storefront uses are reachable (Security-Audit.md, SA-01), and
/// authentication is bearer-only, with no cookie mode (SA-08).
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class AccountEndpointTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const string Password = "Str0ng!Passw0rd";

    [Fact]
    public void Exactly_the_five_account_routes_are_mapped()
    {
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/account", StringComparison.Ordinal) == true)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["GET /api/account/manage/info", "POST /api/account/login", "POST /api/account/logout", "POST /api/account/refresh", "POST /api/account/register"],
            routes);
    }

    [Theory]
    [InlineData("POST", "/api/account/manage/2fa")]
    [InlineData("POST", "/api/account/manage/info")]
    [InlineData("POST", "/api/account/forgotPassword")]
    [InlineData("POST", "/api/account/resetPassword")]
    [InlineData("POST", "/api/account/resendConfirmationEmail")]
    [InlineData("GET", "/api/account/confirmEmail?userId=x&code=y")]
    public async Task Account_management_and_email_flows_are_not_reachable_even_with_a_valid_token(string method, string path)
    {
        var (client, _) = await _factory.SignInAsync($"removed-{Guid.NewGuid():N}@example.com");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { enable = true, twoFactorCode = "000000", email = "x@example.com" }) : null,
        });

        // 405 is what routing answers when another verb is mapped on the same path (GET manage/info).
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} {path} answered {(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_stolen_access_token_can_no_longer_lock_the_owner_out_with_2fa()
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        var (stolen, _) = await _factory.SignInAsync(email, Password);

        var enable = await stolen.PostAsJsonAsync("/api/account/manage/2fa", new { enable = true, twoFactorCode = "123456" });
        var owner = await _factory.CreateClient().PostAsJsonAsync("/api/account/login", new { email, password = Password });

        Assert.Equal(HttpStatusCode.NotFound, enable.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task Account_info_needs_a_bearer_token_and_returns_the_email()
    {
        var email = $"info-{Guid.NewGuid():N}@example.com";
        var (client, _) = await _factory.SignInAsync(email);

        var info = await AuthorizationTests.ReadJson(await client.GetAsync("/api/account/manage/info"));
        var anonymous = await _factory.CreateClient().GetAsync("/api/account/manage/info");

        Assert.Equal(email, info.GetProperty("email").GetString());
        Assert.False(info.GetProperty("isEmailConfirmed").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("useCookies=true")]
    [InlineData("useSessionCookies=true")]
    public async Task Login_never_issues_a_cookie(string query)
    {
        var email = $"cookie-{Guid.NewGuid():N}@example.com";
        await _factory.CreateClient().PostAsJsonAsync("/api/account/register", new { email, password = Password });

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/account/login?{query}", new { email, password = Password });
        var body = await AuthorizationTests.ReadJson(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
    }

    [Fact]
    public async Task An_identity_cookie_is_not_a_way_to_authenticate()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Identity.Application=forged-or-old-value");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/manage/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task Registration_and_login_keep_their_error_contract()
    {
        var email = $"contract-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/account/register", new { email, password = Password })).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/account/register", new { email, password = Password });
        var weak = await client.PostAsJsonAsync("/api/account/register", new { email = "weak-" + email, password = "short" });
        var invalid = await client.PostAsJsonAsync("/api/account/register", new { email = "not-an-email", password = Password });
        var wrong = await client.PostAsJsonAsync("/api/account/login", new { email, password = "Wr0ng!Password" });
        var unknown = await client.PostAsJsonAsync("/api/account/login", new { email = "nobody-" + email, password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.True((await AuthorizationTests.ReadJson(duplicate)).GetProperty("errors").TryGetProperty("DuplicateUserName", out _));
        Assert.Equal("VALIDATION_FAILED", (await AuthorizationTests.ReadJson(weak)).GetProperty("code").GetString());
        Assert.True((await AuthorizationTests.ReadJson(weak)).GetProperty("errors").TryGetProperty("PasswordTooShort", out _));
        Assert.True((await AuthorizationTests.ReadJson(invalid)).GetProperty("errors").TryGetProperty("InvalidEmail", out _));
        foreach (var failed in new[] { wrong, unknown })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            Assert.Equal("Failed", (await AuthorizationTests.ReadJson(failed)).GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task Refresh_returns_new_tokens_and_rejects_garbage()
    {
        var (_, tokens) = await _factory.SignInAsync($"refresh-{Guid.NewGuid():N}@example.com");
        var client = _factory.CreateClient();

        var refreshed = await client.PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken });
        var garbage = await client.PostAsJsonAsync("/api/account/refresh", new { refreshToken = "not-a-token" });
        var empty = await client.PostAsJsonAsync("/api/account/refresh", new { refreshToken = "" });

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var renewed = await refreshed.Content.ReadFromJsonAsync<StorefrontApiFactory.TokenResponse>();
        var orders = _factory.CreateClient();
        orders.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await orders.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);
    }
}
