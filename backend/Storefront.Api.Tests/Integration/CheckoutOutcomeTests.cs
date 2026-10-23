using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storefront.Api.Data;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// A checkout failure is reported as definite (503 DATABASE_UNAVAILABLE / DATABASE_BUSY, which the
/// frontend presents as "nothing was saved") only when COMMIT was never sent. Each case injects a real
/// SqliteException at one stage of the write, then checks the answer against what is actually in the
/// database, and that a retry with the same Idempotency-Key never creates a second order.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class CheckoutOutcomeTests
{
    private const int SqliteBusy = 5;
    private const int SqliteCantOpen = 14;

    private static HttpRequestMessage Checkout(string key, string correlationId) => new(HttpMethod.Post, "/api/checkout")
    {
        Content = JsonContent.Create(AuthorizationTests.Order((2, 1))),
        Headers = { { "Idempotency-Key", key }, { "X-Correlation-Id", correlationId } },
    };

    /// <summary>Counts orders straight from the file, bypassing the app (and its interceptors).</summary>
    private static long OrdersIn(StorefrontApiFactory factory)
    {
        using var connection = new SqliteConnection($"Data Source={factory.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Orders;";
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    // COMMIT never sent: nothing can have been saved, so the failure is definite.
    [InlineData(FaultStage.Insert, SqliteCantOpen, HttpStatusCode.ServiceUnavailable, "DATABASE_UNAVAILABLE", false)]
    [InlineData(FaultStage.Insert, SqliteBusy, HttpStatusCode.ServiceUnavailable, "DATABASE_BUSY", false)]
    // COMMIT failed. Here SQLite didn't save the order, but the app can't prove that, so: unknown.
    [InlineData(FaultStage.BeforeCommit, SqliteCantOpen, HttpStatusCode.InternalServerError, "DATABASE_ERROR", false)]
    [InlineData(FaultStage.BeforeCommit, SqliteBusy, HttpStatusCode.InternalServerError, "DATABASE_ERROR", false)]
    // The order WAS saved but the commit reported an error: must never be called a definite failure.
    [InlineData(FaultStage.AfterCommit, SqliteCantOpen, HttpStatusCode.InternalServerError, "DATABASE_ERROR", true)]
    [InlineData(FaultStage.AfterCommit, SqliteBusy, HttpStatusCode.InternalServerError, "DATABASE_ERROR", true)]
    public async Task A_database_failure_is_definite_only_when_the_order_cannot_have_been_saved(
        FaultStage stage, int sqliteCode, HttpStatusCode expectedStatus, string expectedCode, bool actuallySaved)
    {
        var fault = new DatabaseFault(stage, sqliteCode);
        using var factory = new StorefrontApiFactory(configureServices: services =>
            services.ConfigureDbContext<StorefrontDbContext>(options => options.AddInterceptors(fault)));
        var client = factory.CreateClient();
        Assert.Equal(0, OrdersIn(factory)); // app started and migrated with the fault disarmed
        var key = $"outcome-{stage}-{sqliteCode}";

        fault.Armed = true;
        var failed = await client.SendAsync(Checkout(key, $"first-{stage}-{sqliteCode}"));
        var problem = JsonDocument.Parse(await failed.Content.ReadAsStringAsync()).RootElement;

        Assert.False(fault.Armed); // the fault really fired
        Assert.Equal(expectedStatus, failed.StatusCode);
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.Equal(actuallySaved ? 1 : 0, OrdersIn(factory));
        if (expectedStatus == HttpStatusCode.InternalServerError)
        {
            // An unknown outcome is logged as an error with the exception, under the request's ID.
            var entry = Assert.Single(factory.Logs.Entries, e => e.Message.StartsWith("Request failed: DATABASE_ERROR"));
            Assert.NotNull(entry.Exception);
            Assert.Contains($"CorrelationId:first-{stage}-{sqliteCode}", entry.Scopes);
        }

        // The shopper's Try again: same key, database healthy.
        var retry = await client.SendAsync(Checkout(key, $"retry-{stage}-{sqliteCode}"));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(actuallySaved, retry.Headers.TryGetValues("Idempotent-Replayed", out var replayed) && replayed.Single() == "true");
        Assert.Equal(1, OrdersIn(factory)); // exactly one order either way
    }

    [Fact]
    public async Task After_a_commit_that_succeeded_but_reported_an_error_the_order_is_reconciled_by_key()
    {
        var fault = new DatabaseFault(FaultStage.AfterCommit, SqliteCantOpen);
        using var factory = new StorefrontApiFactory(configureServices: services =>
            services.ConfigureDbContext<StorefrontDbContext>(options => options.AddInterceptors(fault)));
        var client = factory.CreateClient();
        Assert.Equal(0, OrdersIn(factory));

        fault.Armed = true;
        var failed = await client.SendAsync(Checkout("reconcile-0001", "reconcile-first"));
        var problem = JsonDocument.Parse(await failed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("same Idempotency-Key", problem.GetProperty("detail").GetString());
        Assert.Contains("new key could create a second order", problem.GetProperty("detail").GetString());

        // FakeStoreAPI is down by the time the shopper retries: the saved order is still returned,
        // because the key is looked up before the catalogue is needed.
        factory.Catalogue.FailWith = new Storefront.Api.Exceptions.UpstreamServiceException(
            Storefront.Api.Exceptions.UpstreamFailureKind.Unavailable, "down");
        var replay = await client.SendAsync(Checkout("reconcile-0001", "reconcile-retry"));
        var confirmation = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, OrdersIn(factory));

        // The same key with different details is refused, not placed as a second order.
        factory.Catalogue.FailWith = null;
        var changed = new HttpRequestMessage(HttpMethod.Post, "/api/checkout")
        {
            Content = JsonContent.Create(AuthorizationTests.Order((2, 2))),
            Headers = { { "Idempotency-Key", "reconcile-0001" } },
        };
        var refused = await client.SendAsync(changed);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(1, OrdersIn(factory));
        Assert.False(string.IsNullOrEmpty(confirmation.GetProperty("orderReference").GetString()));
    }

    [Fact]
    public async Task An_unknown_outcome_is_never_described_as_unavailable_or_busy()
    {
        // Guards the mapping order: the commit failure wraps a SQLITE_CANTOPEN, which on its own maps to
        // 503 DATABASE_UNAVAILABLE. Wrapped, it must stay an unknown outcome.
        var problem = Storefront.Api.Middleware.GlobalExceptionHandler.Map(
            new Storefront.Api.Exceptions.OrderOutcomeUnknownException(new SqliteException("unable to open database file", SqliteCantOpen)));

        Assert.Equal(500, problem.Status);
        Assert.Equal("DATABASE_ERROR", problem.Extensions["code"]);
        Assert.False(Storefront.Api.Middleware.GlobalExceptionHandler.IsKnownDependencyFailure(
            new Storefront.Api.Exceptions.OrderOutcomeUnknownException(new SqliteException("busy", SqliteBusy))));
    }
}
