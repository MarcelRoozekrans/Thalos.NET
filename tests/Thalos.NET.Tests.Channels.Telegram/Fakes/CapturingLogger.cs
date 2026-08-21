using Microsoft.Extensions.Logging;

namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>
/// Records the <see cref="EventId"/> of every log call it receives. Used where a test needs to prove WHICH code
/// path produced an observable outcome (e.g. an admission gate dropping an update cleanly, versus a defensive
/// catch swallowing an exception that produces the same outward result) — something no assertion on
/// <see cref="TelegramChannelSource.ReadAsync"/>'s output alone can distinguish.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Every <see cref="EventId.Id"/> logged so far, in order.</summary>
    public List<int> EventIds { get; } = [];

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        EventIds.Add(eventId.Id);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
