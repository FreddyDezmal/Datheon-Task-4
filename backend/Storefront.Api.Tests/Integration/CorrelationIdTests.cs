using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Storefront.Api.Exceptions;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// For every kind of error response: the X-Correlation-Id header, the problem body's traceId and the
/// request's log line carry the same ID, and the failure is logged exactly once. Covers errors raised as
/// exceptions (logged by GlobalExceptionHandler) and errors answered without one (logged by
/// CorrelationIdMiddleware): controller 404s, model validation, 401, 429 and unknown routes.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class CorrelationIdTests
{
    private const string HandlerCategory = "Storefront.Api.Middleware.GlobalExceptionHandler";
    private const string MiddlewareCategory = "Storefront.Api.Middleware.CorrelationIdMiddleware";

    private static HttpRequestMessage Json(HttpMethod method, string path, string json) => new(method, path)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    // Formatted with the invariant culture: on a machine whose decimal separator is a comma (en-ZA on
    // Windows, de-DE…) plain interpolation would write 1,00 and produce invalid JSON.
    private static string OrderJson(int productId, decimal expectedTotal) => FormattableString.Invariant(
        $$"""{"customer":{"name":"Ada Lovelace","email":"ada@example.com"},"items":[{"productId":{{productId}},"quantity":1}],"expectedTotal":{{expectedTotal}}}""");

    public static TheoryData<string, HttpStatusCode, string> Cases => new()
    {
        // Raised as exceptions
        { "checkout-validation", HttpStatusCode.BadRequest, HandlerCategory },
        { "price-changed", HttpStatusCode.Conflict, HandlerCategory },
        { "unavailable-product", HttpStatusCode.UnprocessableEntity, HandlerCategory },
        { "upstream-down", HttpStatusCode.BadGateway, HandlerCategory },
        { "database-down", HttpStatusCode.ServiceUnavailable, HandlerCategory },
        // Answered without an exception
        { "malformed-json", HttpStatusCode.BadRequest, MiddlewareCategory },
        { "invalid-product-id", HttpStatusCode.BadRequest, MiddlewareCategory },
        { "product-not-found", HttpStatusCode.NotFound, MiddlewareCategory },
        { "unknown-route", HttpStatusCode.NotFound, MiddlewareCategory },
        { "orders-without-token", HttpStatusCode.Unauthorized, MiddlewareCategory },
        { "rate-limited", HttpStatusCode.TooManyRequests, MiddlewareCategory },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_request_id_matches_across_header_trace_id_and_the_single_log_line(
        string scenario, HttpStatusCode expectedStatus, string loggedBy)
    {
        using var factory = new StorefrontApiFactory(
            accountPermitLimit: scenario == "rate-limited" ? 1 : 1000,
            settings: scenario == "database-down"
                ? new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Storefront"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "x.db")};Mode=ReadOnly",
                    ["Database:ApplyMigrationsOnStartup"] = "false",
                }
                : null);
        var client = factory.CreateClient();
        if (scenario == "upstream-down")
        {
            factory.Catalogue.FailWith = new UpstreamServiceException(UpstreamFailureKind.Unavailable, "down");
        }

        if (scenario == "rate-limited")
        {
            await client.PostAsync("/api/account/login", new StringContent("{}", Encoding.UTF8, "application/json")); // uses the one permit
        }

        var request = scenario switch
        {
            "checkout-validation" => Json(HttpMethod.Post, "/api/checkout", """{"customer":{"name":"","email":"x"},"items":[]}"""),
            "price-changed" => Json(HttpMethod.Post, "/api/checkout", OrderJson(2, 1.00m)),
            "unavailable-product" => Json(HttpMethod.Post, "/api/checkout", OrderJson(999, 1.00m)),
            "upstream-down" => new HttpRequestMessage(HttpMethod.Get, "/api/products"),
            "database-down" => Json(HttpMethod.Post, "/api/checkout", OrderJson(2, 30.07m)),
            "malformed-json" => Json(HttpMethod.Post, "/api/checkout", "{\"customer\":"),
            "invalid-product-id" => new HttpRequestMessage(HttpMethod.Get, "/api/products/0"),
            "product-not-found" => new HttpRequestMessage(HttpMethod.Get, "/api/products/999"),
            "unknown-route" => new HttpRequestMessage(HttpMethod.Get, "/api/no-such-thing"),
            "orders-without-token" => new HttpRequestMessage(HttpMethod.Get, "/api/orders"),
            "rate-limited" => Json(HttpMethod.Post, "/api/account/login", "{}"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var id = $"ref-{scenario}";
        request.Headers.Add("X-Correlation-Id", id);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(id, response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal(id, JsonDocument.Parse(body).RootElement.GetProperty("traceId").GetString());

        var logged = factory.Logs.Entries
            .Where(e => e.Scopes.Contains($"CorrelationId:{id}") && e.Category is HandlerCategory or MiddlewareCategory)
            .ToList();
        var entry = Assert.Single(logged); // logged, and only once
        Assert.Equal(loggedBy, entry.Category);
        Assert.True(entry.Level >= Microsoft.Extensions.Logging.LogLevel.Information);
        if (loggedBy == MiddlewareCategory)
        {
            Assert.Contains($"answered {(int)expectedStatus}", entry.Message);
        }
    }

    [Fact]
    public async Task A_generated_id_is_the_same_in_the_header_the_trace_id_and_the_log()
    {
        using var factory = new StorefrontApiFactory();

        var response = await factory.CreateClient().GetAsync("/api/products/999");
        var id = response.Headers.GetValues("X-Correlation-Id").Single();

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("traceId").GetString());
        Assert.Single(factory.Logs.Entries, e => e.Category == MiddlewareCategory && e.Scopes.Contains($"CorrelationId:{id}"));
    }

    [Fact]
    public async Task Error_log_lines_name_the_route_pattern_not_the_raw_path()
    {
        using var factory = new StorefrontApiFactory();

        await factory.CreateClient().GetAsync("/api/products/987654");
        await factory.CreateClient().GetAsync("/api/secret%0Ainjected-line");

        var lines = factory.Logs.Entries.Where(e => e.Category == MiddlewareCategory).Select(e => e.Message).ToList();
        Assert.Contains(lines, m => m.Contains("api/products/{id") && !m.Contains("987654"));
        Assert.Contains(lines, m => m.Contains("(no matching route)"));
        Assert.DoesNotContain(lines, m => m.Contains("injected-line") || m.Contains("secret"));
    }

    [Fact]
    public async Task Successful_requests_add_no_log_lines()
    {
        using var factory = new StorefrontApiFactory();

        await factory.CreateClient().GetAsync("/api/products");

        Assert.DoesNotContain(factory.Logs.Entries, e => e.Category == MiddlewareCategory);
    }
}
