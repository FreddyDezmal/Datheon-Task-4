using System.Net;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;

namespace Storefront.Api.Services;

/// <summary>
/// Retries outbound FakeStoreAPI requests that fail in a way that is likely to pass on a second try:
/// a network error, an attempt that exceeded <see cref="FakeStoreOptions.AttemptTimeoutSeconds"/>, or
/// 408 / 429 / 500 / 502 / 503 / 504. Only GET and HEAD are retried; they don't change anything
/// upstream. Delays grow exponentially with jitter. The whole sequence stays inside HttpClient.Timeout
/// (<see cref="FakeStoreOptions.TimeoutSeconds"/>), so a caller never waits longer than that.
///
/// The storefront's own checkout is never retried here or anywhere on the server: a retry of an order is
/// only safe from the client, with the same Idempotency-Key.
/// </summary>
public sealed class UpstreamRetryHandler(
    IOptions<FakeStoreOptions> options,
    ILogger<UpstreamRetryHandler> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : DelegatingHandler
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    internal static bool IsRetryableStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var safeToRetry = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var maxAttempts = safeToRetry ? 1 + settings.RetryCount : 1;
        var attemptTimeout = TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds);

        for (var attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(attemptTimeout);
            string reason;
            try
            {
                var response = await base.SendAsync(request, attemptCts.Token);
                if (!IsRetryableStatus(response.StatusCode) || attempt >= maxAttempts)
                {
                    return response;
                }

                reason = $"HTTP {(int)response.StatusCode}";
                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                // This attempt ran out of time (not the caller, not the overall budget): try again.
                reason = "attempt timed out";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Last attempt timed out: report it as a timeout, like HttpClient.Timeout would.
                throw new TaskCanceledException($"FakeStoreAPI did not answer within {settings.AttemptTimeoutSeconds}s.");
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                reason = ex.HttpRequestError.ToString();
            }

            var wait = BackoffDelay(attempt, settings.RetryBaseDelayMilliseconds);
            logger.LogWarning(
                "FakeStoreAPI {Method} {Path} failed ({Reason}); retry {Retry} of {MaxRetries} in {DelayMs} ms.",
                request.Method, request.RequestUri?.AbsolutePath, reason, attempt, maxAttempts - 1, (int)wait.TotalMilliseconds);
            await _delay(wait, cancellationToken);
        }
    }

    /// <summary>base × 2^(attempt-1), plus up to 20% jitter so many clients don't retry in lockstep. Capped.</summary>
    internal static TimeSpan BackoffDelay(int attempt, int baseMilliseconds)
    {
        var exponential = baseMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = exponential * 0.2 * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(Math.Min(exponential + jitter, MaxDelay.TotalMilliseconds));
    }
}
