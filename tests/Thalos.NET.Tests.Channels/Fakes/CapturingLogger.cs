using Microsoft.Extensions.Logging;

namespace Thalos.Tests.Channels.Fakes;

/// <summary>
/// One captured log call: enough to assert a specific <see cref="EventId"/> fired (and, if needed, inspect the
/// exception), without depending on the exact rendered message text.
/// </summary>
public sealed record CapturedLog(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>
/// An <see cref="ILogger{T}"/> that records every call instead of discarding it, so a test can assert a specific
/// <c>LoggerMessage</c> actually fired — <see cref="NullLogger{T}"/> cannot do that, and once a turn runs on a
/// detached task nothing else observes its failure except the log.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLog> _entries = [];

    /// <summary>Every call logged so far, in order. Thread-safe to read.</summary>
    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var entry = new CapturedLog(logLevel, eventId, formatter(state, exception), exception);
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}
