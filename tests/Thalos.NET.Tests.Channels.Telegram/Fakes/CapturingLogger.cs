using Microsoft.Extensions.Logging;

namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>
/// Records the <see cref="EventId"/> of every log call it receives. Used where a test needs to prove WHICH code
/// path produced an observable outcome (e.g. an admission gate dropping an update cleanly, versus a defensive
/// catch swallowing an exception that produces the same outward result) — something no assertion on
/// <see cref="TelegramChannelSource.ReadAsync"/>'s output alone can distinguish.
/// </summary>
/// <remarks>
/// Locked, like the sibling fake in <c>Thalos.NET.Tests.Channels</c>: the adapter tests now drive genuinely
/// concurrent deliveries, and <c>TelegramChannelAdapter</c> logs from inside <c>DeliverAsync</c>'s catch-all. Two
/// racing <see cref="List{T}.Add"/> calls can throw — and that throw would surface out of the adapter, inverting
/// the very "nothing escapes DeliverAsync" guarantee these tests exist to prove.
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<int> _eventIds = [];

    /// <summary>Every <see cref="EventId.Id"/> logged so far, in order. Thread-safe to read.</summary>
    public IReadOnlyList<int> EventIds
    {
        get
        {
            lock (_eventIds)
            {
                return [.. _eventIds];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_eventIds)
        {
            _eventIds.Add(eventId.Id);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
