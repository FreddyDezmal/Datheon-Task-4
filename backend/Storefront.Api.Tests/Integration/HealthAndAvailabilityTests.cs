using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storefront.Api.Exceptions;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Liveness, readiness and behaviour while a dependency is down, through the real pipeline.
/// Failures are injected deterministically: a database path that can't be opened, a schema that was
/// never migrated, and the in-memory FakeStore client told to throw.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class HealthAndAvailabilityTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw, HttpResponseMessage Response)> Get(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(raw).RootElement, raw, response);
    }

    /// <summary>A database file that can't be opened: a missing file in read-only mode.</summary>
    private static Dictionary<string, string?> UnreachableDatabase() => new()
    {
        ["ConnectionStrings:Storefront"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "nope.db")};Mode=ReadOnly",
        ["Database:ApplyMigrationsOnStartup"] = "false",
    };

    [Fact]
    public async Task Liveness_and_readiness_are_healthy_when_everything_works()
    {
        using var factory = new StorefrontApiFactory();
        var client = factory.CreateClient();

        var live = await Get(client, "/health/live");
        var ready = await Get(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Equal("Healthy", live.Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal("Healthy", ready.Body.GetProperty("status").GetString());
        Assert.Equal("Healthy", ready.Body.GetProperty("checks").GetProperty("database").GetString());
        Assert.Contains("no-cache", ready.Response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task The_original_health_path_still_works_as_liveness()
    {
        using var factory = new StorefrontApiFactory();
        var health = await Get(factory.CreateClient(), "/health");

        Assert.Equal(HttpStatusCode.OK, health.Status);
        Assert.Equal("Healthy", health.Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_unreachable_database_fails_readiness_but_not_liveness_and_leaks_nothing()
    {
        using var factory = new StorefrontApiFactory(settings: UnreachableDatabase());
        var client = factory.CreateClient();

        var live = await Get(client, "/health/live");
        var ready = await Get(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.Equal("Unhealthy", ready.Body.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", ready.Body.GetProperty("checks").GetProperty("database").GetString());
        foreach (var secret in new[] { "Data Source", "nope.db", "missing-", "Sqlite", "Exception", "   at " })
        {
            Assert.DoesNotContain(secret, ready.Raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_database_whose_migrations_were_not_applied_is_not_ready()
    {
        // A real database file that exists and opens, but was never migrated (e.g. a deployment that
        // skipped `dotnet ef database update`).
        var path = Path.Combine(Path.GetTempPath(), $"unmigrated-{Guid.NewGuid():N}.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Placeholder (Id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        try
        {
            using var factory = new StorefrontApiFactory(settings: new Dictionary<string, string?>
            {
                ["ConnectionStrings:Storefront"] = $"Data Source={path}",
                ["Database:ApplyMigrationsOnStartup"] = "false",
            });

            var client = factory.CreateClient();
            var ready = await Get(client, "/health/ready");
            var live = await Get(client, "/health/live");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
            Assert.Equal("Unhealthy", ready.Body.GetProperty("checks").GetProperty("database").GetString());
            Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("migration(s) not applied"));
            Assert.Equal(HttpStatusCode.OK, live.Status);
            Assert.Equal("Healthy", live.Body.GetProperty("status").GetString());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestDatabase.TryDelete(path);
        }
    }

    [Fact]
    public async Task Losing_the_database_while_running_fails_readiness_and_recovers_but_liveness_never_changes()
    {
        // The app starts healthy (migrated) with its database in a folder of its own; then the folder
        // disappears (unmounted volume, deleted directory), then it comes back empty, then migrated again.
        var folder = Path.Combine(Path.GetTempPath(), $"storefront-lost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "storefront.db");
        try
        {
            using var factory = new StorefrontApiFactory(databasePath: path);
            var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await Get(client, "/health/ready")).Status);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
            var gone = await Get(client, "/health/ready");
            var liveWhileGone = await Get(client, "/health/live");

            Directory.CreateDirectory(folder); // back, but empty: the schema is missing
            var empty = await Get(client, "/health/ready");
            var liveWhileEmpty = await Get(client, "/health/live");

            using (var scope = factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<Storefront.Api.Data.StorefrontDbContext>().Database.MigrateAsync();
            }

            var restored = await Get(client, "/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, gone.Status);
            Assert.Equal("Unhealthy", gone.Body.GetProperty("checks").GetProperty("database").GetString());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, empty.Status);
            Assert.Equal("Unhealthy", empty.Body.GetProperty("checks").GetProperty("database").GetString());
            Assert.Equal(HttpStatusCode.OK, restored.Status);
            Assert.All(new[] { liveWhileGone, liveWhileEmpty }, live =>
            {
                Assert.Equal(HttpStatusCode.OK, live.Status);
                Assert.Equal("Healthy", live.Body.GetProperty("status").GetString());
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task With_the_database_down_checkout_gets_503_with_retry_after_and_browsing_keeps_working()
    {
        using var factory = new StorefrontApiFactory(settings: UnreachableDatabase());
        var client = factory.CreateClient();

        var checkout = await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));
        var body = await checkout.Content.ReadAsStringAsync();
        var products = await client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, checkout.StatusCode);
        Assert.Equal("DATABASE_UNAVAILABLE", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.Equal("5", checkout.Headers.GetValues("Retry-After").Single());
        Assert.DoesNotContain("Sqlite", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nope.db", body);
        Assert.Equal(HttpStatusCode.OK, products.StatusCode); // the catalogue doesn't need the database

        // Logged as a dependency failure (one line, no stack trace), with the request's correlation ID.
        var entry = Assert.Single(factory.Logs.Entries, e => e.Message.StartsWith("Dependency failure: DATABASE_UNAVAILABLE"));
        Assert.Null(entry.Exception);
        Assert.Contains(entry.Scopes, s => s == $"CorrelationId:{checkout.Headers.GetValues("X-Correlation-Id").Single()}");
    }

    [Fact]
    public async Task During_an_upstream_outage_browsing_serves_the_last_good_catalogue_but_checkout_fails_closed()
    {
        using var factory = new StorefrontApiFactory(settings: new Dictionary<string, string?> { ["FakeStore:CatalogueCacheSeconds"] = "0" });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode); // loads once

        factory.Catalogue.FailWith = new UpstreamServiceException(UpstreamFailureKind.Unavailable, "FakeStoreAPI is down.");

        var products = await client.GetAsync("/api/products");
        var categories = await client.GetAsync("/api/categories");
        var product = await client.GetAsync("/api/products/2");
        var checkout = await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));
        var ready = await Get(client, "/health/ready");
        var live = await Get(client, "/health/live");

        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
        Assert.Equal("true", products.Headers.GetValues("Catalogue-Stale").Single());
        Assert.Equal("true", categories.Headers.GetValues("Catalogue-Stale").Single());
        Assert.Equal(HttpStatusCode.OK, product.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, checkout.StatusCode); // never priced from a stale copy
        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal("Degraded", ready.Body.GetProperty("status").GetString());
        Assert.Equal("Degraded", ready.Body.GetProperty("checks").GetProperty("catalogue").GetString());
        Assert.Equal(HttpStatusCode.OK, live.Status);

        factory.Catalogue.FailWith = null;
        var recovered = await client.GetAsync("/api/products");
        Assert.False(recovered.Headers.Contains("Catalogue-Stale"));
        Assert.Equal("Healthy", (await Get(client, "/health/ready")).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task With_no_catalogue_ever_loaded_an_outage_is_reported_as_502_not_hidden()
    {
        using var factory = new StorefrontApiFactory();
        factory.Catalogue.FailWith = new UpstreamServiceException(UpstreamFailureKind.Timeout, "Timed out.");

        var response = await factory.CreateClient().GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("UPSTREAM_TIMEOUT", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id_that_matches_the_problem_trace_id()
    {
        using var factory = new StorefrontApiFactory();
        var client = factory.CreateClient();

        var ok = await client.GetAsync("/api/products");
        var notFound = await client.GetAsync("/api/products/999");
        var problemTrace = JsonDocument.Parse(await notFound.Content.ReadAsStringAsync()).RootElement.GetProperty("traceId").GetString();

        Assert.Matches("^[0-9a-f]{32}$", ok.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal(notFound.Headers.GetValues("X-Correlation-Id").Single(), problemTrace);
    }

    [Theory]
    [InlineData("gateway-abc.123_X", "gateway-abc.123_X")]
    [InlineData("has spaces", null)]
    [InlineData("new\nline", null)]
    [InlineData("{\"json\":1}", null)]
    public async Task A_caller_supplied_correlation_id_is_kept_only_when_it_is_safe_to_log(string supplied, string? expected)
    {
        using var factory = new StorefrontApiFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);

        var id = (await factory.CreateClient().SendAsync(request)).Headers.GetValues("X-Correlation-Id").Single();

        if (expected is null)
        {
            Assert.Matches("^[0-9a-f]{32}$", id);
        }
        else
        {
            Assert.Equal(expected, id);
        }
    }

    [Fact]
    public async Task Passwords_and_tokens_never_reach_the_logs()
    {
        using var factory = new StorefrontApiFactory();
        const string password = "Sup3r!SecretPassw0rd";
        var (client, tokens) = await factory.SignInAsync("logs@example.com", password);
        await client.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));
        await factory.CreateClient().PostAsJsonAsync("/api/account/login", new { email = "logs@example.com", password = "Wr0ng!Password" });
        await factory.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken });
        var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken + "x");
        await forged.PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));

        var logs = string.Join("\n", factory.Logs.AllText);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.DoesNotContain(password, logs);
        Assert.DoesNotContain("Wr0ng!Password", logs);
        Assert.DoesNotContain(tokens.AccessToken, logs);
        Assert.DoesNotContain(tokens.RefreshToken, logs);
        Assert.DoesNotContain("Bearer ", logs); // no Authorization header values
        Assert.DoesNotContain("ada@example.com", logs); // the checkout's customer email
    }

    [Fact]
    public void Invalid_configuration_stops_the_app_at_startup_with_a_clear_message()
    {
        using var factory = new StorefrontApiFactory(settings: new Dictionary<string, string?>
        {
            ["FakeStore:TimeoutSeconds"] = "5",
            ["FakeStore:AttemptTimeoutSeconds"] = "20",
        });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("AttemptTimeoutSeconds must not be greater than FakeStore:TimeoutSeconds", error.Message);
    }
}
