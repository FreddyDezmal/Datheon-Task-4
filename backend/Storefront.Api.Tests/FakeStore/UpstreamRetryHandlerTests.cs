using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.FakeStore;

[Trait("Category", TestCategories.Unit)]
public sealed class UpstreamRetryHandlerTests
{
    /// <summary>Answers each attempt from a script; counts attempts. "hang" waits until cancelled.</summary>
    private sealed class ScriptedHandler(params object[] script) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = script[Math.Min(Attempts, script.Length - 1)];
            Attempts++;
            return step switch
            {
                HttpStatusCode code => new HttpResponseMessage(code),
                Exception ex => throw ex,
                "hang" => await Hang(cancellationToken),
                _ => throw new InvalidOperationException(),
            };
        }

        private static async Task<HttpResponseMessage> Hang(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class UnreachableException : Exception;

    private static (HttpClient Client, ScriptedHandler Inner, List<TimeSpan> Delays) Build(
        FakeStoreOptions options, params object[] script)
    {
        var inner = new ScriptedHandler(script);
        var delays = new List<TimeSpan>();
        var handler = new UpstreamRetryHandler(Options.Create(options), NullLogger<UpstreamRetryHandler>.Instance,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask; // no real waiting in tests
            })
        { InnerHandler = inner };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://upstream.test/") }, inner, delays);
    }

    private static FakeStoreOptions Options3() => new() { RetryCount = 2, RetryBaseDelayMilliseconds = 100, AttemptTimeoutSeconds = 1, TimeoutSeconds = 10 };

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_transient_status_is_retried_and_the_later_success_is_returned(HttpStatusCode transient)
    {
        var (client, inner, delays) = Build(Options3(), transient, HttpStatusCode.OK);

        var response = await client.GetAsync("products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Attempts);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_limit_and_return_the_last_answer()
    {
        var (client, inner, delays) = Build(Options3(), HttpStatusCode.ServiceUnavailable);

        var response = await client.GetAsync("products");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, inner.Attempts); // 1 + RetryCount
        Assert.Equal(2, delays.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Answers_that_will_not_change_on_a_retry_are_returned_at_once(HttpStatusCode status)
    {
        var (client, inner, _) = Build(Options3(), status);

        var response = await client.GetAsync("products");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task A_network_error_is_retried_and_rethrown_after_the_last_attempt()
    {
        var (client, inner, _) = Build(Options3(), new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("products"));
        Assert.Equal(3, inner.Attempts);
    }

    [Fact]
    public async Task A_hanging_attempt_is_abandoned_after_the_attempt_timeout_and_retried()
    {
        var (client, inner, _) = Build(Options3(), "hang", HttpStatusCode.OK);
        var started = DateTimeOffset.UtcNow;

        var response = await client.GetAsync("products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Attempts);
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task When_every_attempt_hangs_the_caller_gets_a_timeout_not_an_endless_wait()
    {
        var (client, inner, _) = Build(Options3(), "hang");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("products"));
        Assert.Equal(3, inner.Attempts);
    }

    [Fact]
    public async Task The_overall_HttpClient_timeout_caps_retries()
    {
        var options = new FakeStoreOptions { RetryCount = 5, RetryBaseDelayMilliseconds = 0, AttemptTimeoutSeconds = 1, TimeoutSeconds = 1 };
        var (client, inner, _) = Build(options, "hang");
        client.Timeout = TimeSpan.FromSeconds(1.5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("products"));
        Assert.InRange(inner.Attempts, 1, 2); // ran out of budget long before 6 attempts
    }

    [Fact]
    public async Task Requests_that_change_data_are_never_retried()
    {
        var (client, inner, delays) = Build(Options3(), HttpStatusCode.ServiceUnavailable);

        var response = await client.PostAsync("orders", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Attempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_retried()
    {
        var (client, inner, _) = Build(Options3(), "hang");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("products", cts.Token));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task RetryCount_zero_means_a_single_attempt()
    {
        var (client, inner, _) = Build(new FakeStoreOptions { RetryCount = 0, AttemptTimeoutSeconds = 1 }, HttpStatusCode.ServiceUnavailable);

        await client.GetAsync("products");

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public void Backoff_doubles_with_bounded_jitter_and_is_capped()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(UpstreamRetryHandler.BackoffDelay(1, 200).TotalMilliseconds, 200, 240);
            Assert.InRange(UpstreamRetryHandler.BackoffDelay(2, 200).TotalMilliseconds, 400, 480);
            Assert.Equal(2000, UpstreamRetryHandler.BackoffDelay(10, 200).TotalMilliseconds);
        }
    }

    [Fact]
    public void An_attempt_timeout_longer_than_the_total_budget_is_rejected_as_configuration()
    {
        var options = new FakeStoreOptions { TimeoutSeconds = 5, AttemptTimeoutSeconds = 10 };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options, new System.ComponentModel.DataAnnotations.ValidationContext(options), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, r => r.ErrorMessage!.Contains("AttemptTimeoutSeconds"));
    }
}
