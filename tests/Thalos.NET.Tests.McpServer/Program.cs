using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // stdout is the protocol channel; never log to it
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

// `--delay-ms N`: stay silent for N ms before speaking MCP (lets tests exercise dispose-during-connect).
if (int.TryParse(builder.Configuration["delay-ms"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var delayMs) && delayMs > 0)
{
    await Task.Delay(delayMs);
}

// The stdio transport completes when stdin reaches EOF; the SDK's hosted service then stops the host, so the process exits promptly.
await builder.Build().RunAsync();
