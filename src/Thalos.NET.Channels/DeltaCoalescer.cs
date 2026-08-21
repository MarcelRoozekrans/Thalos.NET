using System.Text;

namespace Thalos.Channels;

/// <summary>
/// Accumulates streamed text and decides when a channel should re-render it. The interval is the single, host-wide
/// <see cref="ChannelOptions.FlushInterval"/>: there is no per-channel cadence, so a positive value (the default is
/// one second) suppresses the renders falling inside it for EVERY channel, the console included. Renders that would
/// repeat the previous one are suppressed too, because an unchanged edit is an error on Telegram rather than a no-op.
/// </summary>
/// <remarks>
/// Consequently the last delta an adapter receives is not the answer: whatever streams in during the final interval
/// never renders. <see cref="Flush"/> would produce that residue, but the pump does not call it — adapters read the
/// complete text off <c>TurnCompletedEvent.Result</c> instead, which is authoritative and needs no coalescer state.
/// </remarks>
/// <remarks>Not thread-safe: one coalescer serves one turn, driven by one loop.</remarks>
public sealed class DeltaCoalescer(TimeSpan flushInterval, TimeProvider clock)
{
    private readonly StringBuilder _text = new();
    private readonly TimeSpan _flushInterval = flushInterval;
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private string? _activity;
    private string? _lastRender;
    private long _lastRenderStamp = long.MinValue;

    /// <summary>Everything accumulated so far, without the activity line.</summary>
    public string Text => _text.ToString();

    /// <summary>Sets (or with null clears) the activity line shown above the text; the change forces the next render.</summary>
    public void SetActivity(string? activity)
    {
        _activity = activity;
        _lastRenderStamp = long.MinValue; // force the next TryAppend to render
    }

    /// <summary>
    /// Appends <paramref name="delta"/> and reports whether the channel should render now. When it returns true,
    /// <paramref name="render"/> is the full body to display; when false there is nothing new worth sending.
    /// </summary>
    public bool TryAppend(string delta, out string? render)
    {
        if (!string.IsNullOrEmpty(delta))
        {
            _text.Append(delta);
        }

        var now = _clock.GetTimestamp();
        var due = _lastRenderStamp == long.MinValue
                  || _flushInterval <= TimeSpan.Zero
                  || _clock.GetElapsedTime(_lastRenderStamp, now) >= _flushInterval;

        if (!due)
        {
            render = null;
            return false;
        }

        var candidate = Compose();
        if (string.Equals(candidate, _lastRender, StringComparison.Ordinal))
        {
            render = null;
            return false;
        }

        _lastRender = candidate;
        _lastRenderStamp = now;
        render = candidate;
        return true;
    }

    /// <summary>The final body for the turn: accumulated text with no activity line.</summary>
    public string Flush()
    {
        _activity = null;
        _lastRender = _text.ToString();
        return _lastRender;
    }

    private string Compose() =>
        _activity is null ? _text.ToString() : string.Concat("▸ ", _activity, "\n", _text.ToString());
}
