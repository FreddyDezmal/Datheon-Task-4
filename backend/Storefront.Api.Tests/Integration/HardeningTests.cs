using System.Net;
using System.Net.Http.Json;
using System.Text;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Request-size limits on the account endpoints (SA-10) and the runtime version in the startup log (SA-11).
/// Hosted on real Kestrel, not the in-memory TestServer, because only Kestrel enforces request-size limits.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class HardeningTests : IDisposable
{
    private readonly StorefrontApiFactory _factory;

    public HardeningTests()
    {
        _factory = new StorefrontApiFactory();
        _factory.UseKestrel(0);
        _factory.StartServer();
    }

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/api/account/register")]
    [InlineData("/api/account/login")]
    [InlineData("/api/account/refresh")]
    [InlineData("/api/account/logout")]
    public async Task Account_requests_over_16_KiB_are_refused_with_413_before_being_read(string path)
    {
        var body = "{\"email\":\"x@example.com\",\"password\":\"" + new string('x', 20 * 1024) + "\",\"refreshToken\":\"x\"}";

        var response = await _factory.CreateClient().PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("REQUEST_TOO_LARGE", (await AuthorizationTests.ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ordinary_account_requests_are_well_within_the_limit()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/account/register",
            new { email = $"limit-{Guid.NewGuid():N}@example.com", password = "Str0ng!Passw0rd" + new string('x', 100) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Checkout_keeps_its_own_64_KiB_limit()
    {
        var body = "{\"customer\":{\"name\":\"" + new string('x', 70 * 1024) + "\"}}";

        var response = await _factory.CreateClient().PostAsync("/api/checkout", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task The_startup_log_names_the_exact_runtime()
    {
        await _factory.CreateClient().GetAsync("/health/live");

        var started = Assert.Single(_factory.Logs.Entries, e => e.Message.StartsWith("Storefront API started in", StringComparison.Ordinal));
        Assert.Matches(@" on \.NET \d+\.\d+\.\d+", started.Message);
    }
}
