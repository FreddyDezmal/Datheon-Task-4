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
public sealed class DatabaseHealthCheck(StorefrontDbContext db, ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Readiness: database check failed.");
            return HealthCheckResult.Unhealthy("Database unreachable.");
        }
    }
}

/// <summary>Reports the outcome of the most recent FakeStoreAPI call. Never makes a call itself.</summary>
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
