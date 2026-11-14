namespace Thalos.Tests.Channels.Fakes;

/// <summary>
/// Waits for something the pump does on its own background task. The poll returns as soon as the condition holds, so
/// a passing test pays nothing for the deadline. The deadline only turns a pump that never gets there into a clear
/// failure instead of a hung run, so it sits far above scheduling noise: a loaded CI runner can leave a ready
/// continuation waiting for a thread-pool thread for several seconds, and a 5-second deadline failed there (#532).
/// </summary>
internal static class Eventually
{
    /// <summary>How long a wait runs before it counts as a hang.</summary>
    public static readonly TimeSpan HangDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Polls <paramref name="condition"/> every 10 ms until it holds or <see cref="HangDeadline"/> passes.</summary>
    /// <param name="condition">The state to wait for.</param>
    /// <param name="what">What never happened, for the timeout message.</param>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + HangDeadline;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException($"{what} within {HangDeadline.TotalSeconds:0}s");
    }
}
