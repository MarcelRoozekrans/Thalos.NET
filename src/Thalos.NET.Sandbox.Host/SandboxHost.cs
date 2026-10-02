using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Workspaces;

namespace Thalos.Sandbox.Host;

/// <summary>
/// The in-container server of one run's sandbox. It holds no secret but its own bearer token, executes the run's
/// agent-controlled builds, and serves the run's workspace, build and test, and Roslyn tools over MCP to the trusted API.
/// </summary>
/// <remarks>
/// <para>Routes, every one behind <see cref="BearerTokenMiddleware"/>:</para>
/// <list type="bullet">
/// <item><c>POST /control/import?branch=&amp;commit=&amp;solution=</c>: the body is the run's bundle. 202, or 400, 409, 413.</item>
/// <item><c>GET /control/ready</c>: the import, restore and Roslyn state as JSON.</item>
/// <item><c>POST /control/export</c>: the run's changes as a binary patch against the imported commit. 409 before the import.</item>
/// <item><c>/mcp/workspace</c>, <c>/mcp/sandbox</c>, <c>/mcp/roslyn</c>: MCP, each with only its own tools.</item>
/// </list>
/// <para>
/// The MCP transport is stateless, so the route of each request decides the tool set; in stateful mode a session
/// begun on one route would be served on another.
/// </para>
/// </remarks>
public static class SandboxHost
{
    private const string McpRoute = "/mcp/{source:regex(^(workspace|sandbox|roslyn)$)}";

    /// <summary>
    /// Creates the host's builder: reads <see cref="SandboxSettings"/>, creates the work volume's directories, and
    /// registers the tools, the Roslyn server and the MCP transport.
    /// </summary>
    /// <param name="args">Command-line arguments; <c>--KEY=value</c> pairs override the environment.</param>
    /// <exception cref="InvalidOperationException">The settings are missing or invalid; the host does not start.</exception>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var read = SandboxSettings.Read(builder.Configuration);
        if (read.IsFailure)
        {
            throw new InvalidOperationException($"The sandbox host cannot start: {read.Error.Message}");
        }

        var settings = read.Value;
        CreateWorkDirectories(settings);
        AddServices(builder.Services, settings);
        AddTools(builder.Services, settings);
        return builder;
    }

    private static void AddServices(IServiceCollection services, SandboxSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(new SandboxCaller(settings.RunId));
        services.AddSingleton<ISandboxProcessRunner, SandboxProcessRunner>();
        services.AddSingleton(new SandboxToolOptions());
        services.AddSingleton(new GitWorkspaceOptions { DataRoot = Path.Combine(settings.WorkRoot, "git") });
        services.AddSingleton(sp => new GitCli(sp.GetRequiredService<GitWorkspaceOptions>()));

        services.AddSingleton<LocalRunWorkspace>();
        services.AddSingleton<IRunWorkspaceProvider>(sp => sp.GetRequiredService<LocalRunWorkspace>());
        services.AddSingleton<RestoreService>();
        services.AddSingleton<IRunWorkspaceChangeListener>(sp => sp.GetRequiredService<RestoreService>());
        services.AddSingleton(sp => new RunMcpServerRegistry(
            new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal) { [RoslynProxyTools.ServerName] = RoslynProxyTools.Definition(settings) },
            () => sp.GetRequiredService<LocalRunWorkspace>(), // deferred: the workspace observes the registry
            sp.GetRequiredService<ILoggerFactory>(),
            TimeProvider.System));
        services.AddSingleton<IRunWorkspaceObserver>(sp => sp.GetRequiredService<RunMcpServerRegistry>());
        services.AddSingleton<IRunWorkspaceChangeListener>(sp => sp.GetRequiredService<RunMcpServerRegistry>());
        services.AddSingleton<RoslynProxyTools>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<RouteTools>();
    }

    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    private static void AddTools(IServiceCollection services, SandboxSettings settings)
    {
        services.AddThalos(thalos =>
        {
            if (settings.AllowAnyWriteExtension)
            {
                thalos.UseRunWorkspaceToolsAllowingAnyExtension(o => AddProtected(o, settings));
            }
            else
            {
                thalos.UseRunWorkspaceTools(settings.WriteExtensions, o => AddProtected(o, settings));
            }

            thalos.AddLocalTools(SandboxToolOptions.SourceName, typeof(SandboxTools));
        });

        services.AddMcpServer().WithHttpTransport(o =>
        {
            o.Stateless = true;
            o.ConfigureSessionOptions = async (http, options, ct) =>
            {
                // Replaced, never added to: the collection arrives holding every tool registered in the container.
                options.ToolCollection = [];
                var source = http.Request.RouteValues["source"] as string;
                var tools = await http.RequestServices.GetRequiredService<RouteTools>().ForAsync(source, ct).ConfigureAwait(false);
                foreach (var tool in tools)
                {
                    options.ToolCollection.Add(tool);
                }
            };
        });
    }

    /// <summary>Maps the bearer check and every route onto <paramref name="app"/>.</summary>
    /// <param name="app">The built host.</param>
    /// <returns><paramref name="app"/>.</returns>
    public static WebApplication Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<BearerTokenMiddleware>();
        app.MapPost("/control/import", ImportAsync);
        app.MapGet("/control/ready", Ready);
        app.MapPost("/control/export", ExportAsync);
        app.MapMcp(McpRoute);
        return app;
    }

    private static void AddProtected(RunWorkspaceToolOptions options, SandboxSettings settings)
    {
        foreach (var entry in settings.ProtectedPaths)
        {
            options.ProtectedPaths.Add(entry);
        }
    }

    /// <summary>The work volume hides the image's <c>/work</c>, so its directories are created at every start.</summary>
    private static void CreateWorkDirectories(SandboxSettings settings)
    {
        foreach (var name in (string[])["home", "nuget", "repo"])
        {
            Directory.CreateDirectory(Path.Combine(settings.WorkRoot, name));
        }
    }

    private static async Task<IResult> ImportAsync(HttpContext http, ImportService imports, string? branch, string? commit, string? solution)
    {
        // Kestrel's 30 MB default would cut a bundle off; the import enforces its own cap while it streams.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = ImportService.MaxBundleBytes + 1;
        }

        if (http.Request.ContentLength > ImportService.MaxBundleBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var reply = await imports.StartAsync(branch, commit, solution, http.Request.Body, http.RequestAborted).ConfigureAwait(false);
        return reply switch
        {
            ImportReply.Accepted => Results.StatusCode(StatusCodes.Status202Accepted),
            ImportReply.AlreadyImported => Results.StatusCode(StatusCodes.Status409Conflict),
            ImportReply.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
            _ => Results.StatusCode(StatusCodes.Status400BadRequest),
        };
    }

    private static IResult Ready(LocalRunWorkspace workspaces, RestoreService restore, RoslynProxyTools roslyn, ImportService imports)
    {
        var (restoreState, restoreDetail) = restore.Snapshot;
        var (roslynState, roslynDetail) = roslyn.Snapshot;
        if (imports.Failure is { } failure)
        {
            restoreState = RestoreState.Failed;
            restoreDetail = $"not run: {failure}";
        }

        var report = new ReadyReport(
            workspaces.Current is not null,
            restoreState switch { RestoreState.Ok => "ok", RestoreState.Failed => "failed", _ => "pending" },
            restoreDetail,
            roslynState switch { RoslynState.Ready => "ready", RoslynState.Failed => "failed", _ => "pending" },
            roslynDetail);
        return Results.Json(report, ReadyJsonContext.Default.ReadyReport);
    }

    private static async Task<IResult> ExportAsync(ExportService exports, CancellationToken ct)
    {
        var exported = await exports.ExportAsync(ct).ConfigureAwait(false);
        if (exported.IsFailure)
        {
            return exported.Error.Code == AgentErrorCode.Validation
                ? Results.StatusCode(StatusCodes.Status409Conflict)
                : Results.Text($"{exported.Error.Message} {exported.Error.Detail}", statusCode: StatusCodes.Status500InternalServerError);
        }

        // Deleted when the response has been sent and the stream is disposed.
        var patch = new FileStream(exported.Value, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return Results.Stream(patch, "application/octet-stream");
    }

    /// <summary>The tools each MCP route serves, each run as the <see cref="SandboxCaller"/>.</summary>
    /// <param name="sources">The registered tool sources; <c>workspace</c> and <c>sandbox</c> are read.</param>
    /// <param name="roslyn">The <c>roslyn</c> route's proxied tools.</param>
    /// <param name="restore">Awaited before every <c>sandbox</c> and <c>roslyn</c> call.</param>
    /// <param name="caller">The caller every call runs as.</param>
    internal sealed class RouteTools(IEnumerable<IToolSource> sources, RoslynProxyTools roslyn, RestoreService restore, SandboxCaller caller)
    {
        private IReadOnlyList<McpServerTool>? _workspace;
        private IReadOnlyList<McpServerTool>? _sandbox;

        /// <summary>The tools of the route named <paramref name="source"/>; none for any other name.</summary>
        public async ValueTask<IReadOnlyList<McpServerTool>> ForAsync(string? source, CancellationToken ct) => source switch
        {
            RunWorkspaceToolOptions.SourceName => _workspace ??= await LocalAsync(source, before: null, ct).ConfigureAwait(false),
            SandboxToolOptions.SourceName => _sandbox ??= await LocalAsync(source, restore.EnsureRestoredAsync, ct).ConfigureAwait(false),
            RoslynProxyTools.ServerName => await roslyn.GetToolsAsync(caller, ct).ConfigureAwait(false),
            _ => [],
        };

        private async Task<IReadOnlyList<McpServerTool>> LocalAsync(string name, Func<CancellationToken, ValueTask>? before, CancellationToken ct)
        {
            var source = sources.First(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            var tools = await source.GetToolsAsync(ct).ConfigureAwait(false);
            return tools.IsFailure
                ? []
                : [.. tools.Value.OfType<AIFunction>().Select(f => (McpServerTool)new SandboxScopedTool(McpServerTool.Create(new TextResultFunction(f)), caller, before))];
        }
    }

    /// <summary>
    /// Returns a tool's string result as the string itself. A reflected tool method's result arrives as a
    /// <see cref="System.Text.Json.JsonElement"/>, which MCP would send as JSON, quoted and escaped; the tools' results are
    /// plain text, and the API reads them as such.
    /// </summary>
    /// <param name="inner">The tool function.</param>
    private sealed class TextResultFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            return result is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } text ? text.GetString() : result;
        }
    }
}

/// <summary>The body of <c>GET /control/ready</c>.</summary>
/// <param name="Imported">Whether the run's commit is checked out.</param>
/// <param name="Restore"><c>pending</c>, <c>ok</c> or <c>failed</c>.</param>
/// <param name="RestoreDetail">The tail of the last restore's output, or why it did not run.</param>
/// <param name="Roslyn"><c>pending</c>, <c>ready</c> or <c>failed</c>.</param>
/// <param name="Detail">Why Roslyn failed, or null.</param>
internal sealed record ReadyReport(bool Imported, string Restore, string? RestoreDetail, string Roslyn, string? Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadyReport))]
internal sealed partial class ReadyJsonContext : JsonSerializerContext;
