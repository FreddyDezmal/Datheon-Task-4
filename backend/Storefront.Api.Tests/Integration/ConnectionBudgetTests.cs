using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Storefront.Api.Data;
using Storefront.Api.Health;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// The public readiness probe can't be used to tie up database connections (Security-Audit.md, SA-04):
/// probes that arrive together share one database check.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class ConnectionBudgetTests
{
    /// <summary>Holds every connection open at the gate until released, and counts the opens.</summary>
    private sealed class ConnectionGate : DbConnectionInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Armed { get; set; }
        public int Opened;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Interlocked.Increment(ref Opened);
                Entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task Simultaneous_readiness_probes_share_one_database_check()
    {
        var gate = new ConnectionGate();
        using var factory = new StorefrontApiFactory(configureServices: services =>
            services.ConfigureDbContext<StorefrontDbContext>(options => options.AddInterceptors(gate)));
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode); // warm up
        var probe = factory.Services.GetRequiredService<DatabaseReadinessProbe>();
        var before = probe.ChecksRun;

        gate.Armed = true;
        var probes = Enumerable.Range(0, 100).Select(_ => client.GetAsync("/health/ready")).ToList();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300); // let the other 99 arrive while the first check is still holding its connection
        gate.Release();
        var responses = await Task.WhenAll(probes);
        gate.Armed = false;

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, probe.ChecksRun - before);
        // One check opens the connection twice in sequence (connect test, then the migrations query),
        // never once per caller.
        Assert.InRange(gate.Opened, 1, 2);
    }

    [Fact]
    public async Task A_probe_after_a_finished_check_runs_a_fresh_one()
    {
        using var factory = new StorefrontApiFactory();
        var client = factory.CreateClient();
        var probe = factory.Services.GetRequiredService<DatabaseReadinessProbe>();

        await client.GetAsync("/health/ready");
        var after1 = probe.ChecksRun;
        await client.GetAsync("/health/ready");

        Assert.Equal(after1 + 1, probe.ChecksRun);
    }

    [Fact]
    public async Task An_abandoned_probe_does_not_cancel_the_check_others_are_waiting_for()
    {
        var gate = new ConnectionGate();
        using var factory = new StorefrontApiFactory(configureServices: services =>
            services.ConfigureDbContext<StorefrontDbContext>(options => options.AddInterceptors(gate)));
        var client = factory.CreateClient();
        await client.GetAsync("/health/ready");

        gate.Armed = true;
        using var impatient = new CancellationTokenSource();
        var abandoned = client.GetAsync("/health/ready", impatient.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var patient = client.GetAsync("/health/ready");
        await Task.Delay(100);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        gate.Release();

        Assert.Equal(HttpStatusCode.OK, (await patient).StatusCode);
    }

    [Fact]
    public async Task With_reuse_configured_a_recent_result_is_served_without_touching_the_database()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new StorefrontApiFactory(
            timeProvider: time,
            settings: new Dictionary<string, string?> { ["Health:ReadinessCacheSeconds"] = "5" });
        var client = factory.CreateClient();
        var probe = factory.Services.GetRequiredService<DatabaseReadinessProbe>();

        await client.GetAsync("/health/ready");
        var first = probe.ChecksRun;
        await client.GetAsync("/health/ready");
        var reused = probe.ChecksRun;
        time.Advance(TimeSpan.FromSeconds(6));
        await client.GetAsync("/health/ready");

        Assert.Equal(first, reused);
        Assert.Equal(first + 1, probe.ChecksRun);
    }
}
