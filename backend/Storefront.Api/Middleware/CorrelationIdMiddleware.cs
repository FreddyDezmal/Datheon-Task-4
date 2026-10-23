using System.Text.RegularExpressions;

namespace Storefront.Api.Middleware;

/// <summary>
/// Gives every request one ID that appears in its response header, its ProblemDetails "traceId" and
/// every log line written while handling it, so a shopper's "error reference" leads straight to the logs.
/// A caller (another service, a proxy) may supply its own X-Correlation-Id; it is accepted only if it is
/// short and plain, so it can't be used to inject text into logs. Otherwise a new one is generated.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Set in HttpContext.Items by code that already logged this request's failure.</summary>
    public const string ErrorLoggedKey = "Storefront.ErrorLogged";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        var id = SafeId().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new CorrelationScope(id)))
        {
            await next(context);

            // Errors answered without an exception (a 404 from a controller, model validation, 401,
            // 429, an unknown route) would otherwise leave no log line to match the reference against.
            var status = context.Response.StatusCode;
            if (status >= 400 && !context.Items.ContainsKey(ErrorLoggedKey))
            {
                // The route pattern, not the raw path: no identifiers or caller-controlled text in the log.
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no matching route)";
                logger.Log(
                    status >= 500 ? LogLevel.Warning : LogLevel.Information,
                    "Request {Method} {Route} answered {StatusCode}.",
                    context.Request.Method, route, status);
            }
        }
    }

    /// <summary>
    /// A log scope that structured formatters (JSON) read as a CorrelationId property and plain-text
    /// formatters print as "CorrelationId:…".
    /// </summary>
    private sealed class CorrelationScope(string id) : IReadOnlyList<KeyValuePair<string, object>>
    {
        private readonly KeyValuePair<string, object> _pair = new("CorrelationId", id);

        public KeyValuePair<string, object> this[int index] => index == 0 ? _pair : throw new ArgumentOutOfRangeException(nameof(index));

        public int Count => 1;

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            yield return _pair;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => $"CorrelationId:{_pair.Value}";
    }
}
