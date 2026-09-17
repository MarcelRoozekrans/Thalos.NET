using Microsoft.Extensions.Logging;

namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>
/// One captured log call: the <see cref="EventId"/> plus the structured state fields a <c>LoggerMessage</c>-generated
/// call carries (each placeholder in the message template, e.g. <c>SenderId</c>), so a test can assert on the VALUE
/// a field carries — not just its rendered text — the way <see cref="EventIds"/> alone cannot.
/// </summary>
public sealed record CapturedLog(EventId EventId, IReadOnlyList<KeyValuePair<string, object>> State);

/// <summary>
/// Records the <see cref="EventId"/> and structured state of every log call it receives. Used where a test needs to
/// prove WHICH code path produced an observable outcome (e.g. an admission gate dropping an update cleanly, versus
/// a defensive catch swallowing an exception that produces the same outward result) — something no assertion on
/// <see cref="TelegramChannelSource.ReadAsync"/>'s output alone can distinguish — or to prove a structured field
/// carries the value it claims to (e.g. a numeric id, not its stringified form).
/// </summary>
/// <remarks>
/// Locked, like the sibling fake in <c>Thalos.NET.Tests.Channels</c>: the adapter tests now drive genuinely
/// concurrent deliveries, and <c>TelegramChannelAdapter</c> logs from inside <c>DeliverAsync</c>'s catch-all. Two
/// racing <see cref="List{T}.Add"/> calls can throw — and that throw would surface out of the adapter, inverting
/// the very "nothing escapes DeliverAsync" guarantee these tests exist to prove.
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLog> _entries = [];

    /// <summary>Every <see cref="EventId.Id"/> logged so far, in order. Thread-safe to read.</summary>
    public IReadOnlyList<int> EventIds
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries.Select(e => e.EventId.Id)];
            }
        }
    }

    /// <summary>Every log call captured so far, in order, including each call's structured state. Thread-safe to read.</summary>
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
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // A LoggerMessage-generated call's TState implements IReadOnlyList<KeyValuePair<string, object>> — one
        // entry per message-template placeholder, plus a trailing "{OriginalFormat}". Anything else (a plain
        // logger.LogX call with no structured state) is simply recorded with no fields.
        IReadOnlyList<KeyValuePair<string, object>> fields =
            state as IReadOnlyList<KeyValuePair<string, object>> ?? [];

        lock (_entries)
        {
            _entries.Add(new CapturedLog(eventId, fields));
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
