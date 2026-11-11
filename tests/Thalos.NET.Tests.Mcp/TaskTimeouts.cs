namespace Thalos.Tests.Mcp;

/// <summary>Bounded waits that cancel their timer once the awaited task settles, so no delay outlives the test.</summary>
internal static class TaskTimeouts
{
    /// <summary>Returns <paramref name="task"/> if it settles within <paramref name="timeout"/>, otherwise the elapsed delay.</summary>
    public static async Task<Task> WhenAnyOrDelayAsync(Task task, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource();
        try
        {
            return await Task.WhenAny(task, Task.Delay(timeout, cts.Token));
        }
        finally
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
    }
}
