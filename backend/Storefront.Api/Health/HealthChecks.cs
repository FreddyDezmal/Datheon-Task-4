using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Storefront.Api.Data;
using Storefront.Api.Services;

namespace Storefront.Api.Health;

/*
 * Two probes, for whatever runs the API (a load balancer, an orchestrator, or a person with curl):
 *
 *   GET /health/live   Is the process up and able to answer? Checks nothing else, so an outage of the
 *                      database or FakeStoreAPI never makes a supervisor restart a healthy process.
 *   GET /health/ready  Should this instance receive traffic? Unhealthy (503) when the database can't be
 *                      reached or its schema is behind the code, or while the app is shutting down.
 *                      FakeStoreAPI is external and optional for browsing (a stale copy may be served),
 *                      so its failure only makes readiness "Degraded" (still 200).
 *
 * Responses contain status words only: no connection strings, file paths, exception messages or versions.
 */

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddStorefrontHealthChecks(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseReadinessProbe>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<CatalogueHealthCheck>("catalogue", tags: [ReadyTag])
            .AddCheck<ShutdownHealthCheck>("lifecycle", tags: [ReadyTag]);
        return services;
    }

    public static void MapStorefrontHealthChecks(this IEndpointRouteBuilder app)
    {
        var live = new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync };
        app.MapHealthChecks("/health/live", live);
        app.MapHealthChecks("/health", live); // original path, kept for existing scripts and tests
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteAsync,
        });
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>{"status":"Healthy","checks":{"database":"Healthy",...}}: deliberately nothing more.</summary>
    internal static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}

/// <summary>Can the database be opened, and has every migration been applied?</summary>
/// <summary>Readiness of the database: delegates to the shared <see cref="DatabaseReadinessProbe"/>.</summary>
public sealed class DatabaseHealthCheck(DatabaseReadinessProbe probe) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        probe.CheckAsync(cancellationToken);
}

/// <summary>
/// Runs the database part of readiness: can it connect, and are all migrations applied?
///
/// /health/ready is public and unauthenticated, and each check holds a database connection for two
/// round trips. Run per request, a burst of probes held one connection each and could use up the
/// database's whole connection limit (Security-Audit.md, SA-04). So probes that arrive together share
/// one check: however many callers are waiting, at most one check runs at a time, using one
/// connection. A probe that arrives after a check has finished starts a new one, so the answer is never
/// stale. Optionally (Health:ReadinessCacheSeconds) a finished result is also reused for a few seconds.
/// </summary>
public sealed class DatabaseReadinessProbe(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<DatabaseReadinessProbe> logger)
{
    private readonly object _gate = new();
    private readonly TimeSpan _reuseFor = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Health:ReadinessCacheSeconds", 0), 0, 60));
    private Task<HealthCheckResult>? _inFlight;
    private (DateTimeOffset At, HealthCheckResult Result)? _last;
    private long _checksRun;

    /// <summary>How many database checks have actually run (for tests and diagnostics).</summary>
    public long ChecksRun => Interlocked.Read(ref _checksRun);

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        Task<HealthCheckResult> shared;
        lock (_gate)
        {
            if (_last is { } last && _reuseFor > TimeSpan.Zero && timeProvider.GetUtcNow() - last.At < _reuseFor)
            {
                return Task.FromResult(last.Result);
            }

            shared = _inFlight ??= RunAsync();
        }

        // A caller that gives up stops waiting, without cancelling the check the others share.
        return shared.WaitAsync(cancellationToken);
    }

    private async Task<HealthCheckResult> RunAsync()
    {
        await Task.Yield(); // leave the lock before touching the database
        HealthCheckResult result;
        try
        {
            Interlocked.Increment(ref _checksRun);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            result = await CheckDatabaseAsync(db, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Readiness: database check failed.");
            result = HealthCheckResult.Unhealthy("Database unreachable.");
        }

        lock (_gate)
        {
            _last = (timeProvider.GetUtcNow(), result);
            _inFlight = null;
        }

        return result;
    }

    private async Task<HealthCheckResult> CheckDatabaseAsync(StorefrontDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy("Database unreachable.");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending > 0)
        {
            logger.LogWarning("Readiness: {PendingMigrations} database migration(s) not applied.", pending);
            return HealthCheckResult.Unhealthy("Database schema out of date.");
        }

        return HealthCheckResult.Healthy();
    }
}

public sealed class CatalogueHealthCheck(CatalogueState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var (lastFailure, _, _) = state.Snapshot();
        return Task.FromResult(lastFailure is null
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Product catalogue upstream failing ({lastFailure})."));
    }
}

/// <summary>Turns readiness off as soon as shutdown starts, so a load balancer stops sending new requests.</summary>
public sealed class ShutdownHealthCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthCheckResult.Unhealthy("Shutting down.")
            : HealthCheckResult.Healthy());
}
