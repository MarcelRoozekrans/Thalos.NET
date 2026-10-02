using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Mcp;
using Thalos.Tools;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>Registers in-process tool types whose calls are served by each run's sandbox.</summary>
public static class SandboxToolSourceExtensions
{
    /// <summary>
    /// Adds <paramref name="toolType"/>'s tools as the source named <paramref name="sourceName"/>, served by each run's
    /// sandbox: a <see cref="RemoteRunToolSource.ForLocalSchemas"/> source whose schemas come from a
    /// <see cref="LocalToolSource"/> over <paramref name="toolType"/>, built as <see cref="ThalosBuilder.AddLocalTools"/>
    /// builds one, but never registered as a tool source and never invoked through the remote one. The remote source is
    /// also registered as an <see cref="IRunWorkspaceObserver"/>, so a removed run's client is dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schemas only.</b> The schema source builds <paramref name="toolType"/> from its own container of null objects,
    /// never from the host's: an <see cref="IRunWorkspaceProvider"/> that finds no workspace, an
    /// <see cref="ISandboxProcessRunner"/> that throws, <see cref="RunWorkspaceToolOptions"/> that allow no write,
    /// default <see cref="SandboxToolOptions"/> and no logging. A schema function invoked directly therefore finds no
    /// workspace and touches no file. <paramref name="toolType"/>'s constructor may take only those, an
    /// <see cref="ILogger{TCategoryName}"/> or a sequence of <see cref="IRunWorkspaceChangeListener"/>; another
    /// dependency fails the first <see cref="IToolSource.GetToolsAsync"/>.
    /// </para>
    /// <para>
    /// <b>Calls.</b> A caller with no run claim is refused. A run caller's call goes to the endpoint the registered
    /// <see cref="IRunToolEndpointResolver"/> returns for the run and <paramref name="sourceName"/>, found on first use;
    /// with none registered, every run call is refused. The registered <see cref="RemoteRunToolOptions"/>, or its
    /// defaults, bound it, and each registered <see cref="IRunToolCallObserver"/> is told of it.
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="sourceName">The source's name, e.g. <see cref="RunWorkspaceToolOptions.SourceName"/>; the sandbox serves the same tools at <c>/mcp/{sourceName}</c>.</param>
    /// <param name="toolType">A <see cref="ThalosToolTypeAttribute"/> class, e.g. <see cref="WorkspaceTools"/> or <see cref="SandboxTools"/>.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceName"/> violates <see cref="ToolSourceName"/>, <paramref name="toolType"/> is not marked
    /// <see cref="ThalosToolTypeAttribute"/>, or an MCP entry, a local tool source such as <c>UseRunWorkspaceTools</c>'s,
    /// or another remote run tool source already has <paramref name="sourceName"/>. The check holds in either order: a
    /// later local source or MCP entry by this name is refused too, because the tool catalog keeps the first source of a
    /// name and a local one would serve a run's calls on the host.
    /// </exception>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static ThalosBuilder AddRemoteRunTools(this ThalosBuilder builder, string sourceName, Type toolType)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ToolSourceName.ThrowIfInvalid(sourceName, nameof(sourceName));
        ArgumentNullException.ThrowIfNull(toolType);
        if (!toolType.IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
        {
            throw new ArgumentException($"Type '{toolType.FullName}' is not marked [ThalosToolType].", nameof(toolType));
        }

        McpThalosBuilderExtensions.AddRemote(builder.Services, sourceName, sp => RemoteRunToolSource.ForLocalSchemas(
            sourceName,
            new SchemaToolSource(sourceName, toolType),
            new DeferredRunToolEndpointResolver(sp),
            McpThalosBuilderExtensions.RemoteOptions(sp),
            McpThalosBuilderExtensions.LoggerFactory(sp),
            McpThalosBuilderExtensions.Clock(sp),
            McpThalosBuilderExtensions.RunToolCallObservers(sp)));
        return builder;
    }

    /// <summary>A <see cref="LocalToolSource"/> over its own container of null objects; it owns and disposes the container.</summary>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    private sealed class SchemaToolSource : IToolSource, IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly LocalToolSource _tools;

        public SchemaToolSource(string name, Type toolType)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IRunWorkspaceProvider, NoRunWorkspaces>();
            services.AddSingleton<ISandboxProcessRunner, NoProcesses>();
            services.AddSingleton(new SandboxToolOptions());
            services.AddSingleton(new RunWorkspaceToolOptions { AllowedWriteExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) });
            _services = services.BuildServiceProvider();
            _tools = new LocalToolSource(name, _services, [toolType]);
        }

        public string Name => _tools.Name;

        public ValueTask<Result<IReadOnlyList<Microsoft.Extensions.AI.AITool>, AgentError>> GetToolsAsync(CancellationToken ct) => _tools.GetToolsAsync(ct);

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    /// <summary>Finds no workspace, so a schema tool invoked directly has no run to act on.</summary>
    private sealed class NoRunWorkspaces : IRunWorkspaceProvider
    {
        private static readonly AgentError SchemaOnly = AgentError.Validation("This provider only supports tool schemas; it has no workspaces.");

        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<RunWorkspace, AgentError>.Failure(SchemaOnly));

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => ValueTask.FromResult<RunWorkspace?>(null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<RunWorkspace>>([]);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => ValueTask.FromResult(UnitResult<AgentError>.Success());
    }

    /// <summary>Never runs a process: with no workspace found first, it is never reached.</summary>
    private sealed class NoProcesses : ISandboxProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct) =>
            throw new InvalidOperationException("The schema-only sandbox tools never run a process.");
    }
}
