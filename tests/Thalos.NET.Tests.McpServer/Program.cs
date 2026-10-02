using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Thalos.Tests.McpServer;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // stdout is the protocol channel; never log to it
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

// `--pid-file PATH`: write this process's id to PATH before anything else, so a test can check the process exited
// even when it was stopped before it ever answered an MCP call.
if (builder.Configuration["pid-file"] is { Length: > 0 } pidFile)
{
    await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
}

// `--dump-env PATH`: write this process's whole environment to PATH as NAME=value lines and exit, without speaking MCP,
// so a test can stand this program in for any child process and see exactly what it was started with.
if (builder.Configuration["dump-env"] is { Length: > 0 } envFile)
{
    var lines = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(e => $"{e.Key}={e.Value}");
    await File.WriteAllLinesAsync(envFile, lines);
    return 0;
}

// `--fail-first-start PATH`: exit at once, before speaking MCP, unless PATH exists; creates PATH, so only the first start fails.
if (builder.Configuration["fail-first-start"] is { Length: > 0 } failMarker && !File.Exists(failMarker))
{
    await File.WriteAllTextAsync(failMarker, "failed once");
    return 1;
}

// `--delay-ms N`: stay silent for N ms before speaking MCP (lets tests exercise dispose-during-connect).
if (TryMilliseconds("delay-ms", out var delay))
{
    await Task.Delay(delay);
}

// `--ready-after N`: the ready_after tool fails until N ms after process start.
if (TryMilliseconds("ready-after", out var readyAfter))
{
    EchoTools.ReadyAfter = readyAfter;
}

// `--ready-when PATH`: the ready_after tool also fails until PATH exists.
EchoTools.ReadyWhen = builder.Configuration["ready-when"];

// `--ready-tool NAME`: also offer a tool named NAME that behaves exactly as ready_after, for a host whose ready tool
// is fixed, such as the sandbox host's `list_solutions`. Without the flag the tool list is unchanged.
if (builder.Configuration["ready-tool"] is { Length: > 0 } readyTool)
{
    builder.Services.AddSingleton(McpServerTool.Create(EchoTools.ReadyAfterTool, new McpServerToolCreateOptions { Name = readyTool }));
}

// `--reload-delay-ms N`: reload_count takes N ms after counting. `--call-log PATH`: ready_after and reload_count append their name to PATH.
if (TryMilliseconds("reload-delay-ms", out var reloadDelay))
{
    EchoTools.ReloadDelay = reloadDelay;
}

// `--reload-when PATH`: reload_count also waits, after counting, until PATH exists, so a test decides when a reload ends.
EchoTools.ReloadWhen = builder.Configuration["reload-when"];

EchoTools.CallLog = builder.Configuration["call-log"];

// The stdio transport completes when stdin reaches EOF; the SDK's hosted service then stops the host, so the process exits promptly.
await builder.Build().RunAsync();

// `--shutdown-delay-ms N`: stay alive for N ms after stdin closes, as a server slow to shut down would.
if (TryMilliseconds("shutdown-delay-ms", out var shutdownDelay))
{
    await Task.Delay(shutdownDelay);
}

return 0;

bool TryMilliseconds(string key, out TimeSpan value)
{
    var parsed = int.TryParse(builder.Configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0;
    value = TimeSpan.FromMilliseconds(parsed ? ms : 0);
    return parsed;
}
