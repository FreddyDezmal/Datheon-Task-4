using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Storefront.Api.Data;
using Storefront.Api.Exceptions;
using Storefront.Api.Services;
using Storefront.Api.Tests.Checkout;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// The behaviour that matters most for production, against a real PostgreSQL server: the migrations,
/// health, and above all checkout idempotency and outcome reporting. Each test gets its own database.
/// Runs only when STOREFRONT_TEST_POSTGRES is set (see <see cref="PostgreSqlTestDatabase"/>).
/// </summary>
[Trait("Category", TestCategories.PostgreSql)]
public sealed class PostgreSqlTests
{
    private static HttpRequestMessage Checkout(string? key, object body, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static Task<long> Orders(PostgreSqlTestDatabase database) => database.ScalarAsync("SELECT COUNT(*) FROM \"Orders\"");

    // ---------- Schema ----------

    [PostgreSqlFact]
    public async Task The_migrations_build_the_schema_with_exact_money_utc_times_and_the_idempotency_index()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using var db = database.CreateContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --context PostgreSqlStorefrontDbContext");
        Assert.Equal(1, await database.ScalarAsync(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'Orders' AND column_name = 'Total' AND data_type = 'numeric'"));
        Assert.Equal(1, await database.ScalarAsync(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'Orders' AND column_name = 'CreatedAtUtc' AND data_type = 'timestamp with time zone'"));
        Assert.Equal(1, await database.ScalarAsync(
            "SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_Orders_IdempotencyKey' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexdef LIKE '%WHERE (\"IdempotencyKey\" IS NOT NULL)%'"));
    }

    [PostgreSqlFact]
    public async Task Money_and_times_round_trip_exactly()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        var client = new InMemoryFakeStoreClient(TestData.Catalogue());
        var request = CheckoutRequestValidatorTests.ValidRequest((2, 2), (19, 1));
        request.ExpectedTotal = 56.75m;

        await using (var db = database.CreateContext())
        {
            await TestData.CheckoutService(client, db).CheckoutAsync(request, CheckoutCaller.Guest, CancellationToken.None);
        }

        await using var read = database.CreateContext();
        var order = await read.Orders.Include(o => o.Lines).SingleAsync();
        Assert.Equal(52.55m, order.Subtotal);
        Assert.Equal(4.20m, order.Tax);
        Assert.Equal(0m, order.Shipping);
        Assert.Equal(56.75m, order.Total);
        Assert.Equal(22.30m, order.Lines.Single(l => l.ProductId == 2).UnitPrice);
        Assert.Equal(TestData.Now.UtcDateTime, order.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, order.CreatedAtUtc.Kind);
    }

    [PostgreSqlFact]
    public async Task A_check_constraint_rejects_a_negative_total_at_the_database()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO \"Orders\" (\"Reference\",\"CreatedAtUtc\",\"CustomerName\",\"CustomerEmail\",\"Currency\",\"Subtotal\",\"Tax\",\"Shipping\",\"Total\") " +
            "VALUES ('DEMO-NEG', now(), 'A', 'a@example.com', 'USD', 1, 0, 0, -0.01)", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("23514", error.SqlState); // check_violation
        Assert.True(DatabaseErrors.IsConstraintViolation(error));
    }

    // ---------- Health ----------

    [PostgreSqlFact]
    public async Task Readiness_and_liveness_on_postgresql()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        using var factory = new StorefrontApiFactory(settings: database.AppSettings());
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", (await Json(ready)).GetProperty("checks").GetProperty("database").GetString());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [PostgreSqlFact]
    public async Task An_unmigrated_postgresql_database_is_not_ready_but_the_process_is_live()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(migrate: false);
        using var factory = new StorefrontApiFactory(settings: database.AppSettings(new() { ["Database:ApplyMigrationsOnStartup"] = "false" }));
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("migration(s) not applied"));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [PostgreSqlFact]
    public async Task An_unreachable_server_fails_readiness_and_checkout_definitely_without_leaking_the_connection_string()
    {
        // Nothing listens on port 1: the connection is refused before anything is sent.
        var unreachable = new NpgsqlConnectionStringBuilder(PostgreSqlTestDatabase.AdminConnectionString!)
        {
            Port = 1, Database = "storefront_unreachable", Timeout = 3,
        }.ConnectionString;
        using var factory = new StorefrontApiFactory(settings: new Dictionary<string, string?>
        {
            ["Database:Provider"] = "PostgreSql",
            ["ConnectionStrings:Storefront"] = unreachable,
            ["Database:ApplyMigrationsOnStartup"] = "false",
        });
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");
        var checkout = await client.SendAsync(Checkout("pg-unreachable-0001", AuthorizationTests.Order((2, 1))));
        var body = await checkout.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, checkout.StatusCode);
        Assert.Equal("DATABASE_UNAVAILABLE", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        var password = new NpgsqlConnectionStringBuilder(unreachable).Password;
        Assert.DoesNotContain("storefront_unreachable", body);
        Assert.DoesNotContain("Host=", body);
        if (!string.IsNullOrEmpty(password))
        {
            Assert.DoesNotContain(password, body);
            Assert.DoesNotContain(password, string.Join("\n", factory.Logs.AllText));
        }
    }

    // ---------- Checkout idempotency ----------

    [PostgreSqlFact]
    public async Task A_replay_returns_the_saved_order_and_a_changed_order_with_the_same_key_is_refused()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        using var factory = new StorefrontApiFactory(settings: database.AppSettings());
        var client = factory.CreateClient();

        var first = await client.SendAsync(Checkout("pg-replay-0001", AuthorizationTests.Order((2, 1))));
        var replay = await client.SendAsync(Checkout("pg-replay-0001", AuthorizationTests.Order((2, 1))));
        var changed = await client.SendAsync(Checkout("pg-replay-0001", AuthorizationTests.Order((2, 2))));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await Json(first)).GetProperty("orderReference").GetString(), (await Json(replay)).GetProperty("orderReference").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await Json(changed)).GetProperty("code").GetString());
        Assert.Equal(1, await Orders(database));
    }

    [PostgreSqlFact]
    public async Task Simultaneous_submissions_with_one_key_create_exactly_one_order()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        using var factory = new StorefrontApiFactory(settings: database.AppSettings());
        var client = await factory.CreateSignedInClientAsync("pg-burst@example.com");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.SendAsync(Checkout("pg-double-click-0001", AuthorizationTests.Order((2, 1))))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var references = await Task.WhenAll(responses.Select(async r => (await Json(r)).GetProperty("orderReference").GetString()));
        Assert.Single(references.Distinct());
        Assert.Equal(1, await Orders(database));
    }

    [PostgreSqlFact]
    public async Task Losing_the_insert_race_returns_the_winners_order_instead_of_failing_or_duplicating()
    {
        // Both requests pass the "already ordered?" lookup; the other one commits first, so this one's
        // insert hits the unique index (23505) and must answer with the winner's order.
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        var client = new InMemoryFakeStoreClient(TestData.Catalogue());
        var request = CheckoutRequestValidatorTests.ValidRequest((2, 1));
        request.ExpectedTotal = 30.07m;
        var caller = new CheckoutCaller(null, "pg-race-loser-0001");
        CheckoutResult? winner = null;
        var race = new BeforeFirstSave(async () =>
        {
            await using var other = database.CreateContext();
            winner = await TestData.CheckoutService(client, other).CheckoutAsync(request, caller, CancellationToken.None);
        });

        await using var db = database.CreateContext(race);
        var loser = await TestData.CheckoutService(client, db).CheckoutAsync(request, caller, CancellationToken.None);

        Assert.NotNull(winner);
        Assert.False(winner.IsReplay);
        Assert.True(loser.IsReplay);
        Assert.Equal(winner.Confirmation.OrderReference, loser.Confirmation.OrderReference);
        Assert.Equal(1, await Orders(database));
    }

    [PostgreSqlFact]
    public async Task Losing_the_race_to_a_different_order_with_the_same_key_is_refused_not_merged()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        var client = new InMemoryFakeStoreClient(TestData.Catalogue());
        var caller = new CheckoutCaller(null, "pg-race-other-0001");
        var other = CheckoutRequestValidatorTests.ValidRequest((2, 1));
        other.ExpectedTotal = 30.07m;
        var mine = CheckoutRequestValidatorTests.ValidRequest((2, 2), (19, 1));
        mine.ExpectedTotal = 56.75m;
        var race = new BeforeFirstSave(async () =>
        {
            await using var otherDb = database.CreateContext();
            await TestData.CheckoutService(client, otherDb).CheckoutAsync(other, caller, CancellationToken.None);
        });

        await using var db = database.CreateContext(race);
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            TestData.CheckoutService(client, db).CheckoutAsync(mine, caller, CancellationToken.None));

        Assert.Equal(1, await Orders(database));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM \"OrderLines\""));
    }

    private static Exception PgError(string sqlState) => new PostgresException("Injected PostgreSQL error.", "ERROR", "ERROR", sqlState);

    private static Exception PgConnectionLost() =>
        new NpgsqlException("Exception while reading from stream", new IOException("Connection reset by peer"));

    public static TheoryData<FaultStage, string, HttpStatusCode, string, bool> Outcomes => new()
    {
        // COMMIT never sent: nothing can have been saved.
        { FaultStage.Insert, "connection-lost", HttpStatusCode.ServiceUnavailable, "DATABASE_UNAVAILABLE", false },
        { FaultStage.Insert, "55P03", HttpStatusCode.ServiceUnavailable, "DATABASE_BUSY", false },
        // COMMIT failed: unknown, whatever the error.
        { FaultStage.BeforeCommit, "connection-lost", HttpStatusCode.InternalServerError, "DATABASE_ERROR", false },
        { FaultStage.BeforeCommit, "40001", HttpStatusCode.InternalServerError, "DATABASE_ERROR", false },
        // The order WAS saved but the commit reported an error.
        { FaultStage.AfterCommit, "connection-lost", HttpStatusCode.InternalServerError, "DATABASE_ERROR", true },
        { FaultStage.AfterCommit, "57P01", HttpStatusCode.InternalServerError, "DATABASE_ERROR", true },
    };

    [PostgreSqlTheory]
    [MemberData(nameof(Outcomes))]
    public async Task A_database_failure_is_definite_only_when_the_order_cannot_have_been_saved_and_the_retry_reconciles(
        FaultStage stage, string error, HttpStatusCode expectedStatus, string expectedCode, bool actuallySaved)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        var fault = new DatabaseFault(stage, () => error == "connection-lost" ? PgConnectionLost() : PgError(error));
        using var factory = new StorefrontApiFactory(
            settings: database.AppSettings(),
            configureServices: services =>
                services.ConfigureDbContext<PostgreSqlStorefrontDbContext>(options => options.AddInterceptors(fault)));
        var client = factory.CreateClient();
        Assert.Equal(0, await Orders(database));
        var key = $"pg-outcome-{stage}-{error}";

        fault.Armed = true;
        var failed = await client.SendAsync(Checkout(key, AuthorizationTests.Order((2, 1)), $"pg-first-{stage}-{error}"));
        var problem = await Json(failed);

        Assert.False(fault.Armed);
        Assert.Equal(expectedStatus, failed.StatusCode);
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.Equal(actuallySaved ? 1 : 0, await Orders(database));

        // Try again with the same key, while FakeStoreAPI happens to be down: a saved order is still returned.
        factory.Catalogue.FailWith = actuallySaved ? new UpstreamServiceException(UpstreamFailureKind.Unavailable, "down") : null;
        var retry = await client.SendAsync(Checkout(key, AuthorizationTests.Order((2, 1))));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(actuallySaved, retry.Headers.TryGetValues("Idempotent-Replayed", out var replayed) && replayed.Single() == "true");
        Assert.Equal(1, await Orders(database));

        // And the same key with different details never becomes a second order.
        factory.Catalogue.FailWith = null;
        var changed = await client.SendAsync(Checkout(key, AuthorizationTests.Order((2, 2))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal(1, await Orders(database));
    }

    // ---------- Accounts and persistence ----------

    [PostgreSqlFact]
    public async Task Accounts_lockout_and_order_history_work_on_postgresql()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        using var factory = new StorefrontApiFactory(settings: database.AppSettings());
        var (client, _) = await factory.SignInAsync("pg-ada@example.com");

        var placed = await client.SendAsync(Checkout("pg-account-0001", AuthorizationTests.Order((2, 1))));
        var orders = await Json(await client.GetAsync("/api/orders"));
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        Assert.Equal(1, orders.GetArrayLength());

        // Lockout writes LockoutEnd, a timestamp with time zone (Npgsql requires UTC offsets).
        var anonymous = factory.CreateClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await anonymous.PostAsJsonAsync("/api/account/login", new { email = "pg-ada@example.com", password = "Wr0ng!Password" });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, last!.StatusCode);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM \"AspNetUsers\" WHERE \"LockoutEnd\" > now()"));
    }

    [PostgreSqlFact]
    public async Task Sign_ins_survive_a_new_instance_because_the_keys_live_in_postgresql()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();

        await DataProtectionKeyTests.SurviveNewInstances(
            () => new StorefrontApiFactory(settings: database.AppSettings()),
            () => database.ScalarAsync("SELECT COUNT(*) FROM \"DataProtectionKeys\""),
            () => database.ScalarAsync("WITH d AS (DELETE FROM \"DataProtectionKeys\" RETURNING 1) SELECT COUNT(*) FROM d"));
    }

    [PostgreSqlFact]
    public async Task With_a_certificate_the_keys_are_stored_encrypted_in_postgresql()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        var settings = database.AppSettings(TestCertificates.Settings(TestCertificates.A));

        StorefrontApiFactory.TokenResponse tokens;
        using (var first = new StorefrontApiFactory(settings: settings))
        {
            (_, tokens) = await first.SignInAsync("pg-encrypted@example.com");
        }

        Assert.True(await database.ScalarAsync("SELECT COUNT(*) FROM \"DataProtectionKeys\"") >= 1);
        Assert.Equal(0, await database.ScalarAsync(
            "SELECT COUNT(*) FROM \"DataProtectionKeys\" WHERE \"Xml\" NOT LIKE '%<encryptedSecret%' OR \"Xml\" LIKE '%unencrypted form%'"));

        using var second = new StorefrontApiFactory(settings: settings);
        var client = second.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Orders_and_accounts_survive_an_app_restart()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        string reference;
        using (var first = new StorefrontApiFactory(settings: database.AppSettings()))
        {
            var (client, _) = await first.SignInAsync("pg-restart@example.com");
            var placed = await client.SendAsync(Checkout("pg-restart-0001", AuthorizationTests.Order((2, 1))));
            reference = (await Json(placed)).GetProperty("orderReference").GetString()!;
        }

        using var second = new StorefrontApiFactory(settings: database.AppSettings());
        var (again, _) = await second.SignInAsync("pg-restart@example.com");
        var orders = await Json(await again.GetAsync("/api/orders"));
        var replay = await again.SendAsync(Checkout("pg-restart-0001", AuthorizationTests.Order((2, 1))));

        Assert.Equal(reference, orders[0].GetProperty("orderReference").GetString());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, await Orders(database));
    }
}
