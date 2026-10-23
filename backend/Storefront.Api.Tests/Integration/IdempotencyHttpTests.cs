using Storefront.Api.Tests.TestSupport;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storefront.Api.Data;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Retries and duplicate submissions through the whole HTTP pipeline (routing, auth, model binding,
/// the exception handler and the real SQLite database), not just the checkout service.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class IdempotencyHttpTests : IDisposable
{
    private readonly StorefrontApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage Checkout(string key, object body) => new(HttpMethod.Post, "/api/checkout")
    {
        Content = JsonContent.Create(body),
        Headers = { { "Idempotency-Key", key } },
    };

    private async Task<List<(string Reference, string? UserId)>> SavedOrdersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<StorefrontDbContext>().Orders
            .Select(o => new { o.Reference, o.UserId })
            .ToListAsync())
            .Select(o => (o.Reference, o.UserId))
            .ToList();
    }

    [Fact]
    public async Task Simultaneous_submissions_with_one_key_create_exactly_one_order()
    {
        var client = await _factory.CreateSignedInClientAsync("burst@example.com");
        var body = AuthorizationTests.Order((2, 1));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SendAsync(Checkout("double-click-0001", body))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var references = await Task.WhenAll(responses.Select(async r =>
            (await AuthorizationTests.ReadJson(r)).GetProperty("orderReference").GetString()));
        Assert.Single(references.Distinct());
        Assert.Equal(5, responses.Count(r => r.Headers.TryGetValues("Idempotent-Replayed", out var v) && v.Single() == "true"));
        Assert.Single(await SavedOrdersAsync());

        var mine = await AuthorizationTests.ReadJson(await client.GetAsync("/api/orders"));
        Assert.Equal(1, mine.GetArrayLength());
    }

    [Fact]
    public async Task A_retry_after_a_lost_response_returns_the_saved_order_instead_of_a_second_one()
    {
        var client = await _factory.CreateSignedInClientAsync("lost@example.com");
        var body = AuthorizationTests.Order((2, 1));

        // The first attempt reaches the server and is saved; the client never reads the response.
        (await client.SendAsync(Checkout("lost-response-01", body))).Dispose();
        var retry = await client.SendAsync(Checkout("lost-response-01", body));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        var saved = Assert.Single(await SavedOrdersAsync());
        Assert.Equal(saved.Reference, (await AuthorizationTests.ReadJson(retry)).GetProperty("orderReference").GetString());
    }

    [Fact]
    public async Task Without_a_key_every_submission_is_a_new_order()
    {
        var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));
        await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));

        Assert.Equal(2, (await SavedOrdersAsync()).Count);
    }

    [Fact]
    public async Task Another_shopper_reusing_a_key_gets_422_and_learns_nothing_about_the_original_order()
    {
        var owner = await _factory.CreateSignedInClientAsync("owner@example.com");
        var other = await _factory.CreateSignedInClientAsync("other@example.com");
        var body = AuthorizationTests.Order((2, 1));
        var original = await AuthorizationTests.ReadJson(await owner.SendAsync(Checkout("shared-key-0001", body)));

        var response = await other.SendAsync(Checkout("shared-key-0001", body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", text);
        Assert.DoesNotContain(original.GetProperty("orderReference").GetString()!, text);
        Assert.DoesNotContain("owner@example.com", text);
        Assert.Single(await SavedOrdersAsync());
    }

    [Fact]
    public async Task A_guest_key_cannot_be_replayed_by_a_signed_in_shopper()
    {
        var body = AuthorizationTests.Order((2, 1));
        (await _factory.CreateClient().SendAsync(Checkout("guest-key-00001", body))).EnsureSuccessStatusCode();
        var signedIn = await _factory.CreateSignedInClientAsync("later@example.com");

        var response = await signedIn.SendAsync(Checkout("guest-key-00001", body));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal(0, (await AuthorizationTests.ReadJson(await signedIn.GetAsync("/api/orders"))).GetArrayLength());
    }

    [Fact]
    public async Task A_rejected_attempt_does_not_use_up_the_key()
    {
        var client = _factory.CreateClient();
        var stale = new
        {
            customer = new { name = "Ada Lovelace", email = "ada@example.com" },
            items = new[] { new { productId = 2, quantity = 1 } },
            expectedTotal = 1.00m,
        };

        var conflict = await client.SendAsync(Checkout("retry-after-409", stale));
        var confirmed = await client.SendAsync(Checkout("retry-after-409", AuthorizationTests.Order((2, 1))));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Single(await SavedOrdersAsync());
    }

    [Theory]
    [InlineData("text/plain", HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE")]
    [InlineData("application/json", HttpStatusCode.BadRequest, "VALIDATION_FAILED")]
    public async Task Bad_request_bodies_get_problem_details_with_a_code(string contentType, HttpStatusCode status, string code)
    {
        var content = new StringContent("{ not json", Encoding.UTF8, contentType);

        var response = await _factory.CreateClient().PostAsync("/api/checkout", content);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await AuthorizationTests.ReadJson(response)).GetProperty("code").GetString());
        Assert.Empty(await SavedOrdersAsync());
    }
}
