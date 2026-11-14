namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>
/// The two kinds of wait in these tests. A wait for something that will happen ends as soon as it does, so its
/// deadline only turns a hang into a failure and sits far above scheduling noise: a loaded CI runner can leave a ready
/// continuation waiting for a thread-pool thread for several seconds, and 5-second deadlines failed there (#532). A
/// window that proves something does not happen always runs to its end, so it stays short.
/// </summary>
internal static class TestDeadlines
{
    /// <summary>How long a wait for an expected result runs before it counts as a hang.</summary>
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    /// <summary>How long a test watches for something that must not happen.</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);
}
