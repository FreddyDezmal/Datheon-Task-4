using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// Records every log entry the app writes (message, exception and active scopes, as text), so tests can
/// check what reaches the logs: that failures are visible, carry the correlation ID, and never contain
/// secrets.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    /// <summary>Everything that was logged, as one string per entry.</summary>
    public IEnumerable<string> AllText => Entries.Select(e => e.ToString());

    public sealed record CapturedLog(string Category, LogLevel Level, string Message, string? Exception, IReadOnlyList<string> Scopes)
    {
        public override string ToString() => $"{Level} {Category}: {Message} {Exception} [{string.Join(" | ", Scopes)}]";
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            owner._scopes.ForEachScope((scope, list) => list.Add(scope?.ToString() ?? string.Empty), scopes);
            owner.Entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception?.ToString(), scopes));
        }
    }
}
