using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Server;

namespace Thalos.Tests.McpServer;

[McpServerToolType]
public static class EchoTools
{
    private static int s_reloads;

    /// <summary>How long after process start <c>ready_after</c> keeps failing; set from <c>--ready-after</c>.</summary>
    public static TimeSpan ReadyAfter { get; set; }

    /// <summary>How long <c>reload_count</c> takes after counting; set from <c>--reload-delay-ms</c>.</summary>
    public static TimeSpan ReloadDelay { get; set; }

    /// <summary>A file <c>ready_after</c> and <c>reload_count</c> append their name to when called; set from <c>--call-log</c>.</summary>
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

    [McpServerTool(Name = "reload_count"), Description("Counts its own calls and returns the count")]
    public static async Task<string> ReloadCount()
    {
        var count = Interlocked.Increment(ref s_reloads).ToString(CultureInfo.InvariantCulture);
        await LogCallAsync("reload_count");
        await Task.Delay(ReloadDelay);
        return count;
    }

    private static async Task LogCallAsync(string tool)
    {
        if (CallLog is { } log)
        {
            await File.AppendAllTextAsync(log, tool + "\n");
        }
    }

    [McpServerTool(Name = "pid"), Description("Returns the server's process id")]
    public static int Pid() => Environment.ProcessId;
}
