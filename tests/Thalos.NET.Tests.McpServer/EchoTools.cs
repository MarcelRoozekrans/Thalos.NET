using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Server;

namespace Thalos.Tests.McpServer;

[McpServerToolType]
public static class EchoTools
{
    private static readonly SemaphoreSlim s_logLock = new(1, 1);
    private static int s_reloads;
    private static int s_reloading;
    private static int s_slowCalls;
    private static int s_overlaps;

    /// <summary>How long after process start <c>ready_after</c> keeps failing; set from <c>--ready-after</c>.</summary>
    public static TimeSpan ReadyAfter { get; set; }

    /// <summary>How long <c>reload_count</c> takes after counting; set from <c>--reload-delay-ms</c>.</summary>
    public static TimeSpan ReloadDelay { get; set; }

    /// <summary>A file <c>ready_after</c>, <c>reload_count</c> and <c>slow</c> append their name to when called; set from <c>--call-log</c>.</summary>
    public static string? CallLog { get; set; }

    [McpServerTool(Name = "echo"), Description("Echoes the input")]
    public static string Echo([Description("Text to echo")] string text) => $"echo:{text}";

    [McpServerTool(Name = "add"), Description("Adds two numbers")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool(Name = "fail"), Description("Always fails")]
    public static string Fail() => throw new InvalidOperationException("boom");

    [McpServerTool(Name = "env"), Description("Returns the value of an environment variable of the server process")]
    public static string Env([Description("Variable name")] string name) => Environment.GetEnvironmentVariable(name) ?? "<unset>";

    [McpServerTool(Name = "args"), Description("Returns the server process's command line")]
    public static string Args() => string.Join(' ', Environment.GetCommandLineArgs());

    [McpServerTool(Name = "cwd"), Description("Returns the server process's working directory")]
    public static string Cwd() => Environment.CurrentDirectory;

    [McpServerTool(Name = "ready_after"), Description("Fails until --ready-after milliseconds have passed since the process started")]
    public static async Task<string> ReadyAfterTool()
    {
        await LogCallAsync("ready_after");
        using var self = Process.GetCurrentProcess();
        return DateTime.Now - self.StartTime >= ReadyAfter ? "ready" : throw new InvalidOperationException("not ready yet");
    }

    [McpServerTool(Name = "reload_count"), Description("Counts its own calls and returns the count; records an overlap with a running slow call")]
    public static async Task<string> ReloadCount()
    {
        Interlocked.Increment(ref s_reloading);
        try
        {
            if (Volatile.Read(ref s_slowCalls) > 0)
            {
                Interlocked.Increment(ref s_overlaps);
            }

            var count = Interlocked.Increment(ref s_reloads).ToString(CultureInfo.InvariantCulture);
            await LogCallAsync("reload_count");
            await Task.Delay(ReloadDelay);
            return count;
        }
        finally
        {
            Interlocked.Decrement(ref s_reloading);
        }
    }

    [McpServerTool(Name = "slow"), Description("Takes the given milliseconds; records an overlap with a running reload_count")]
    public static async Task<string> Slow([Description("Milliseconds")] int ms)
    {
        Interlocked.Increment(ref s_slowCalls);
        try
        {
            if (Volatile.Read(ref s_reloading) > 0)
            {
                Interlocked.Increment(ref s_overlaps);
            }

            await LogCallAsync("slow");
            await Task.Delay(ms);
            return "slow done";
        }
        finally
        {
            Interlocked.Decrement(ref s_slowCalls);
        }
    }

    [McpServerTool(Name = "overlaps"), Description("How many times reload_count and slow ran at the same time")]
    public static int Overlaps() => Volatile.Read(ref s_overlaps);

    private static async Task LogCallAsync(string tool)
    {
        if (CallLog is { } log)
        {
            await s_logLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(log, tool + "\n");
            }
            finally
            {
                s_logLock.Release();
            }
        }
    }

    [McpServerTool(Name = "pid"), Description("Returns the server's process id")]
    public static int Pid() => Environment.ProcessId;
}
