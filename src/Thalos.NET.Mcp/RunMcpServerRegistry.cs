using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Mcp;

/// <summary>
/// Owns one private stdio process per run-scoped MCP server per workflow run, started against that run's workspace
/// with <see cref="RunScopedMcpDefinition"/>'s substituted arguments, environment and working directory. Register one
/// instance as the <see cref="IRunWorkspaceObserver"/>, <see cref="IRunWorkspaceChangeListener"/> and
/// <see cref="IRunToolServerReadiness"/>, so a run's servers follow its workspace's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> <see cref="OnReadyAsync"/> starts every configured server for the run and returns without waiting
/// for any of them; each start is kept as a task that <see cref="WaitAllReadyAsync"/> and
/// <see cref="GetReadyClientAsync"/> observe, so nothing is fire-and-forget. <see cref="OnRemovingAsync"/> and
/// <see cref="DisposeAsync"/> stop every affected server, including one still starting, and return only once its
/// process has been shut down, so the workspace directory is no longer any process's working directory when the
/// provider deletes it. <see cref="DisposeAsync"/> also waits for a removal's stop that is already under way.
/// </para>
/// <para>
/// <b>A removed run's server is never started again.</b> <c>GitWorktreeWorkspaceProvider</c> marks the workspace
/// record as removing before it calls <see cref="OnRemovingAsync"/>, and its <see cref="IRunWorkspaceProvider.FindAsync"/>
/// reports only ready records, so a <see cref="WaitAllReadyAsync"/> that looks the run up after the removal began,
/// while the git side is still running or after it failed, finds nothing and starts nothing. A lookup whose
/// <see cref="IRunWorkspaceProvider.FindAsync"/> was already in flight, and read the record before it was marked, is
/// marked by <see cref="OnRemovingAsync"/> and fails instead of starting a server. That mark lives only as long as the
/// lookup, so nothing accumulates per removed run.
/// </para>
/// <para>
/// <b>Not re-entrant into the provider.</b> <c>GitWorktreeWorkspaceProvider</c> calls <see cref="OnReadyAsync"/> and
/// <see cref="OnRemovingAsync"/> while it holds its per-repository lock. Neither method calls any of the provider's
/// methods, for any repository, and neither waits for a server to become ready: a change that did would deadlock
/// the provider or hold its lock for as long as a server takes to load. Only <see cref="WaitAllReadyAsync"/> calls the provider, and only
/// <see cref="IRunWorkspaceProvider.FindAsync"/>.
/// </para>
/// <para>
/// <b>Reloads.</b> <see cref="OnFilesChanged"/> only counts a change, so it is cheap, never blocks on a server, and
/// does not care in which order notifications for one path arrive. The next <see cref="GetReadyClientAsync"/> for the
/// run applies the reload under the server's lock before it hands the client out. It records the count it is
/// applying before the reload runs, so a change reported while a reload is in flight is reloaded again on the call
/// after. Reloading
/// re-evaluates the workspace's build files; see <see cref="RunScopedMcpDefinition.Reload"/> for why that is only
/// safe while a run cannot write MSBuild files.
/// </para>
/// <para>
/// <b>Leases.</b> <see cref="GetReadyClientAsync"/> hands out a <see cref="RunMcpClientLease"/>, not a bare client. A
/// lease is shared: any number of calls may hold one at once. A reload is exclusive: it holds the server's lock, so no
/// new lease is handed out, and waits until every outstanding lease is disposed before it calls the reload tool or
/// restarts the server. A call is therefore never cut off by a restart and never overlaps a reload. A caller must
/// dispose its lease as soon as its call returns, and must hold at most one lease per run at a time, across all of
/// the run's servers. A file change marks every server of the run for a reload, so a caller holding a lease on server
/// A while asking for one on server B waits for B's reload, which waits for every lease on B; if another caller holds
/// B while asking for A, the two wait for each other forever. Removal and dispose do not wait for leases: they cancel
/// a waiting reload and stop the server, and a call still running on it then fails.
/// </para>
/// <para>
/// <b>No provider.</b> <paramref name="workspaces"/> is optional (ruling R7): a host with the workflow engine off
/// registers no <see cref="IRunWorkspaceProvider"/> but may still declare <c>runScoped</c> in <c>.mcp.json</c>. With
/// none, no run can have a server: <see cref="OnReadyAsync"/> starts nothing, and
/// <see cref="GetReadyClientAsync"/> and <see cref="WaitAllReadyAsync"/> fail for every run id with "no run workspace
/// provider is registered", so run callers are refused, never served by the host server. Host callers never reach
/// the registry and are served as before.
/// </para>
/// </remarks>
/// <param name="runScopedServers">
/// The <c>.mcp.json</c> entries that declare <see cref="McpServerDefinition.RunScoped"/>, keyed by source name. Each
/// must be a stdio server with a command and a valid <see cref="RunScopedMcpDefinition.Reload"/>.
/// </param>
/// <param name="workspaces">Finds a run's workspace after a host restart; <see langword="null"/> when the host has no run workspaces.</param>
/// <param name="loggerFactory">Creates the registry's logger and is passed to the MCP SDK.</param>
/// <param name="clock">Times the connect timeout, the ready-tool polling and <see cref="WaitAllReadyAsync"/>'s timeout.</param>
/// <exception cref="ArgumentException">A server entry is not a valid run-scoped stdio definition.</exception>
public sealed partial class RunMcpServerRegistry(
    IReadOnlyDictionary<string, McpServerDefinition> runScopedServers, IRunWorkspaceProvider? workspaces,
    ILoggerFactory loggerFactory, TimeProvider clock)
    : IRunWorkspaceObserver, IRunWorkspaceChangeListener, IRunToolServerReadiness, IAsyncDisposable
{
    private const string RootPlaceholderValue = "${run.workspace.root}";
    private const string ToolReloadPrefix = "tool:";
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(2);

    private readonly ServerSpec[] _servers = Validate(runScopedServers);
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly ILogger _logger = loggerFactory.CreateLogger<RunMcpServerRegistry>(); // after _loggerFactory's null check: initializers run in order
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry[]> _runs = [];
    private readonly Dictionary<Guid, List<Lookup>> _lookups = [];
    private readonly HashSet<Task> _stops = [];
    private bool _disposed;

    /// <summary>Starts every configured server for <paramref name="workspace"/>'s run in the background, and returns without waiting for any.</summary>
    /// <remarks>
    /// Does nothing when the run already has servers, when no server or no workspace provider is configured, or once
    /// the registry is disposed. <paramref name="ct"/> is not passed to the starts: they belong to the run, not to the
    /// call that announced it.
    /// </remarks>
    /// <param name="workspace">The run's workspace.</param>
    /// <param name="ct">Unused; the starts are stopped by <see cref="OnRemovingAsync"/> or <see cref="DisposeAsync"/>.</param>
    public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (_servers.Length == 0 || workspaces is null)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                LogReadyAfterDispose(_logger, workspace.RunId);
                return ValueTask.CompletedTask;
            }

            if (!_runs.ContainsKey(workspace.RunId))
            {
                _runs[workspace.RunId] = StartRun(workspace);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Stops every server of <paramref name="workspace"/>'s run, including one still starting, and waits until each process is shut down.</summary>
    /// <param name="workspace">The run's workspace, about to be removed.</param>
    /// <param name="ct">Unused: a removal always finishes stopping the run's servers, which is bounded by each server's shutdown timeout.</param>
    public async ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Entry[]? entries;
        TaskCompletionSource? stopped = null;
        lock (_sync)
        {
            _runs.Remove(workspace.RunId, out entries);
            foreach (var lookup in _lookups.GetValueOrDefault(workspace.RunId) ?? [])
            {
                lookup.Removed = true;
            }

            if (entries is not null)
            {
                // Registered in the lock that took the entries: a concurrent DisposeAsync either stops them itself or waits for this stop.
                stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stops.Add(stopped.Task);
            }
        }

        if (entries is null || stopped is null)
        {
            return;
        }

        try
        {
            await StopAllAsync(entries).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _stops.Remove(stopped.Task);
            }

            stopped.TrySetResult();
        }
    }

    /// <summary>Marks the run's servers as needing a reload before their next routed call; cheap and never blocks on a server.</summary>
    /// <param name="runId">The run whose workspace changed.</param>
    /// <param name="relativePaths">The changed paths; not inspected, since any change makes the run's servers stale.</param>
    public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths)
    {
        Entry[]? entries;
        lock (_sync)
        {
            _runs.TryGetValue(runId, out entries);
        }

        if (entries is null)
        {
            return; // no server for the run yet: a start loads the workspace as it is then
        }

        foreach (var entry in entries)
        {
            Interlocked.Increment(ref entry.Changes);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A run with no servers in this process, such as after a host restart, is looked up through the workspace
    /// provider and started. A server whose start failed is started again. The timeout bounds only this wait: a
    /// server that is not ready in time keeps starting, and a later call can still see it ready.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public async ValueTask<UnitResult<AgentError>> WaitAllReadyAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        if (_servers.Length == 0)
        {
            return UnitResult<AgentError>.Success();
        }

        if (workspaces is null)
        {
            return UnitResult<AgentError>.Failure(NoProvider(runId));
        }

        var run = await FindOrStartRunAsync(workspaces, runId, ct).ConfigureAwait(false);
        if (run.IsFailure)
        {
            return UnitResult<AgentError>.Failure(run.Error);
        }

        var entries = run.Value;
        foreach (var entry in entries)
        {
            await RestartIfFailedAsync(entry, ct).ConfigureAwait(false);
        }

        var starts = Array.ConvertAll(entries, e => e.Starting);
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            await Task.WhenAny(Task.WhenAll(starts), Task.Delay(timeout, _clock, timer.Token)).ConfigureAwait(false);
            await timer.CancelAsync().ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        for (var i = 0; i < starts.Length; i++)
        {
            if (!starts[i].IsCompleted)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError(
                    $"Run-scoped MCP server '{entries[i].Spec.Name}' for run {runId} was not ready within {timeout}."));
            }
        }

        foreach (var start in starts)
        {
            var started = await start.ConfigureAwait(false);
            if (started.IsFailure)
            {
                return UnitResult<AgentError>.Failure(started.Error);
            }
        }

        return UnitResult<AgentError>.Success();
    }

    /// <summary>
    /// A shared lease on the ready client of <paramref name="serverName"/> for <paramref name="runId"/>, after applying a
    /// pending reload. Fails, and never falls back, when the run has no ready server: the caller must not reach the
    /// host server instead. Dispose the lease as soon as the call returns; a pending reload waits for it.
    /// </summary>
    /// <remarks>
    /// Never starts a server: <see cref="OnReadyAsync"/> and <see cref="WaitAllReadyAsync"/> do. A server that is still
    /// starting is waited for, bounded only by <paramref name="ct"/>; use <see cref="WaitAllReadyAsync"/> for a bounded
    /// wait. A server whose start or <c>restart</c> reload failed stays failed until the next
    /// <see cref="WaitAllReadyAsync"/> starts it again; a failed <c>tool:</c> reload is retried on the next call.
    /// </remarks>
    /// <param name="serverName">The run-scoped server's source name.</param>
    /// <param name="runId">The calling run.</param>
    /// <param name="ct">Cancels the wait for the server's lock, its start, outstanding leases and its reload call; never the server.</param>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public async ValueTask<Result<RunMcpClientLease, AgentError>> GetReadyClientAsync(string serverName, Guid runId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        if (workspaces is null)
        {
            return Result<RunMcpClientLease, AgentError>.Failure(NoProvider(runId));
        }

        var entry = Array.Find(TryGetRun(runId) ?? [], e => string.Equals(e.Spec.Name, serverName, StringComparison.Ordinal));
        if (entry is null)
        {
            return Result<RunMcpClientLease, AgentError>.Failure(NotRunning(serverName, runId));
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Removed)
            {
                return Result<RunMcpClientLease, AgentError>.Failure(NotRunning(serverName, runId));
            }

            var started = await entry.Starting.WaitAsync(ct).ConfigureAwait(false);
            var target = Volatile.Read(ref entry.Changes);
            var ready = started.IsSuccess && entry.Spec.Reload != ReloadKind.None && target != entry.AppliedChanges
                ? await ReloadAsync(entry, started.Value, target, ct).ConfigureAwait(false)
                : started;
            if (ready.IsFailure)
            {
                return Result<RunMcpClientLease, AgentError>.Failure(ready.Error);
            }

            // Taken under the lock: a reload holds the lock while it waits for the count to reach zero.
            Interlocked.Increment(ref entry.Leases);
            return Result<RunMcpClientLease, AgentError>.Success(new RunMcpClientLease(ready.Value, entry.ReleaseLease));
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Stops every server of every run, including those still starting, waits until each process is shut down, and
    /// waits for any <see cref="OnRemovingAsync"/> already stopping servers. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        Task[] removals;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = [.. _runs.Values.SelectMany(run => run)];
            removals = [.. _stops];
            _runs.Clear();
        }

        await StopAllAsync(entries).ConfigureAwait(false);
        await Task.WhenAll(removals).ConfigureAwait(false); // a removal's stop that was already under way
    }

    // ---------- starting ----------

    /// <summary>Creates the run's entries and begins each start. Called under <see cref="_sync"/>; each start yields before doing any work.</summary>
    private Entry[] StartRun(RunWorkspace workspace)
    {
        var entries = Array.ConvertAll(_servers, spec => new Entry(spec, workspace));
        foreach (var entry in entries)
        {
            entry.Starting = StartAsync(entry);
        }

        return entries;
    }

    /// <summary>The run's entries; after a host restart, looks the workspace up and starts them, unless the run was removed meanwhile.</summary>
    private async ValueTask<Result<Entry[], AgentError>> FindOrStartRunAsync(IRunWorkspaceProvider provider, Guid runId, CancellationToken ct)
    {
        if (TryGetRun(runId) is { } running)
        {
            return Result<Entry[], AgentError>.Success(running);
        }

        var lookup = new Lookup();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_lookups.TryGetValue(runId, out var inFlight))
            {
                inFlight = [];
                _lookups[runId] = inFlight;
            }

            inFlight.Add(lookup);
        }

        // On success the lookup is un-tracked inside StartFound's lock, in the same section that reads its mark, so no
        // OnRemovingAsync can run between the two and go unseen. Every other path un-tracks it here.
        var handedOver = false;
        try
        {
            var workspace = await provider.FindAsync(runId, ct).ConfigureAwait(false);
            if (workspace is null)
            {
                return Result<Entry[], AgentError>.Failure(AgentError.ProviderError($"No workspace is recorded for run {runId}, so it has no run-scoped MCP server."));
            }

            handedOver = true;
            return StartFound(workspace, lookup);
        }
        finally
        {
            if (!handedOver)
            {
                lock (_sync)
                {
                    Untrack(runId, lookup);
                }
            }
        }
    }

    /// <summary>Stops tracking <paramref name="lookup"/>. Called under <see cref="_sync"/>.</summary>
    private void Untrack(Guid runId, Lookup lookup)
    {
        var inFlight = _lookups[runId];
        inFlight.Remove(lookup);
        if (inFlight.Count == 0)
        {
            _lookups.Remove(runId);
        }
    }

    /// <summary>
    /// Un-tracks <paramref name="lookup"/> and starts the servers of the workspace it found, unless the run was removed
    /// while it was in flight; both happen in one lock section, so a removal is either seen here or comes after.
    /// </summary>
    private Result<Entry[], AgentError> StartFound(RunWorkspace workspace, Lookup lookup)
    {
        var runId = workspace.RunId;
        lock (_sync)
        {
            Untrack(runId, lookup);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.TryGetValue(runId, out var entries))
            {
                return Result<Entry[], AgentError>.Success(entries); // started meanwhile, possibly by a new OnReadyAsync
            }

            if (lookup.Removed)
            {
                return Result<Entry[], AgentError>.Failure(AgentError.ProviderError($"The workspace of run {runId} was removed, so it has no run-scoped MCP server."));
            }

            entries = StartRun(workspace);
            _runs[runId] = entries;
            return Result<Entry[], AgentError>.Success(entries);
        }
    }

    private async Task RestartIfFailedAsync(Entry entry, CancellationToken ct)
    {
        if (!entry.Starting.IsCompleted || (await entry.Starting.ConfigureAwait(false)).IsSuccess)
        {
            return;
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked under the lock: a concurrent caller may have restarted it, or the run may be stopping.
            if (!entry.Removed && entry.Starting.IsCompleted && (await entry.Starting.ConfigureAwait(false)).IsFailure)
            {
                entry.Starting = StartAsync(entry);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Starts one server and waits until it is ready. Never throws: every failure, including being stopped, is a
    /// result, because the task is awaited by every caller that observes the entry.
    /// </summary>
    private async Task<Result<McpClient, AgentError>> StartAsync(Entry entry)
    {
        // Return to the caller before any work: OnReadyAsync runs under the provider's lock and must not wait for a process.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        var name = entry.Spec.Name;
        var runId = entry.Workspace.RunId;
        var options = TransportOptions(entry.Spec, entry.Workspace);
        if (options.IsFailure)
        {
            LogStartRefused(_logger, name, runId, options.Error.Message);
            return Result<McpClient, AgentError>.Failure(options.Error);
        }

        var stopping = entry.Stopping.Token;
        McpClient? client = null;
        try
        {
            LogStarting(_logger, name, runId);
            IList<McpClientTool> tools;
            using (var connectTimeout = new CancellationTokenSource(entry.Spec.Definition.Timeout, _clock))
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stopping, connectTimeout.Token))
            {
                client = await McpClient.CreateAsync(new StdioClientTransport(options.Value, _loggerFactory), clientOptions: null, _loggerFactory, connect.Token).ConfigureAwait(false);
                tools = await client.ListToolsAsync(cancellationToken: connect.Token).ConfigureAwait(false);
            }

            if (MissingTool(entry.Spec, tools) is { } missing)
            {
                LogStartRefused(_logger, name, runId, missing.Message);
                return Result<McpClient, AgentError>.Failure(missing); // finally shuts the process down
            }

            await PollReadyToolAsync(client, entry.Spec.Definition.RunScoped!.ReadyTool, stopping).ConfigureAwait(false);

            LogReady(_logger, name, runId);
            var ready = client;
            client = null;
            return Result<McpClient, AgentError>.Success(ready);
        }
        catch (Exception ex) when (stopping.IsCancellationRequested)
        {
            LogStartStopped(_logger, ex, name, runId);
            return Result<McpClient, AgentError>.Failure(AgentError.ProviderError(
                $"Run-scoped MCP server '{name}' for run {runId} was stopped before it was ready.", ex.GetType().Name));
        }
        catch (Exception ex)
        {
            LogStartFailed(_logger, ex, name, runId, ex.Message);
            return Result<McpClient, AgentError>.Failure(AgentError.ProviderError(
                $"Run-scoped MCP server '{name}' for run {runId} failed to start.", ex.GetType().Name)); // the message is logged; Detail carries no raw exception text by policy
        }
        finally
        {
            if (client is not null)
            {
                await DisposeClientAsync(entry, client).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Calls <paramref name="readyTool"/> every <see cref="ReadyPollInterval"/> until a call does not come back as an error; returns at once when there is none.</summary>
    private async Task PollReadyToolAsync(McpClient client, string? readyTool, CancellationToken stopping)
    {
        if (readyTool is null)
        {
            return;
        }

        while ((await client.CallToolAsync(readyTool, arguments: null, progress: null, options: null, stopping).ConfigureAwait(false)).IsError == true)
        {
            await Task.Delay(ReadyPollInterval, _clock, stopping).ConfigureAwait(false);
        }
    }

    /// <summary>A configured ready or reload tool the server does not offer: a configuration error, reported instead of polled forever.</summary>
    private static AgentError? MissingTool(ServerSpec spec, IList<McpClientTool> tools)
    {
        foreach (var (role, tool) in new[] { ("ready", spec.Definition.RunScoped!.ReadyTool), ("reload", spec.ReloadTool) })
        {
            if (tool is not null && !tools.Any(t => string.Equals(t.Name, tool, StringComparison.Ordinal)))
            {
                return AgentError.ProviderError($"Run-scoped MCP server '{spec.Name}' does not offer its {role} tool '{tool}'.");
            }
        }

        return null;
    }

    private static Result<StdioClientTransportOptions, AgentError> TransportOptions(ServerSpec spec, RunWorkspace workspace)
    {
        var runScoped = spec.Definition.RunScoped!;
        var args = new List<string>();
        foreach (var arg in runScoped.Args ?? spec.Definition.Args ?? [])
        {
            var substituted = Substitute(arg, spec.Name, workspace);
            if (substituted.IsFailure)
            {
                return Result<StdioClientTransportOptions, AgentError>.Failure(substituted.Error);
            }

            args.Add(substituted.Value);
        }

        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var layer in new[] { spec.Definition.Env, runScoped.Env })
        {
            foreach (var (key, value) in layer ?? new Dictionary<string, string>(StringComparer.Ordinal))
            {
                var substituted = Substitute(value, spec.Name, workspace);
                if (substituted.IsFailure)
                {
                    return Result<StdioClientTransportOptions, AgentError>.Failure(substituted.Error);
                }

                env[key] = substituted.Value;
            }
        }

        var cwd = Substitute(runScoped.Cwd ?? RootPlaceholderValue, spec.Name, workspace);
        if (cwd.IsFailure)
        {
            return Result<StdioClientTransportOptions, AgentError>.Failure(cwd.Error);
        }

        return Result<StdioClientTransportOptions, AgentError>.Success(new StdioClientTransportOptions
        {
            Name = spec.Name,
            Command = spec.Definition.Command!,
            Arguments = args,
            EnvironmentVariables = env,
            WorkingDirectory = cwd.Value,
            ShutdownTimeout = spec.Definition.ShutdownTimeout,
        });
    }

    /// <summary>Replaces <c>${run.id}</c>, <c>${run.workspace.root}</c> and <c>${run.workspace.solution}</c>; leaves any other <c>${...}</c> as it is.</summary>
    private static Result<string, AgentError> Substitute(string value, string server, RunWorkspace workspace)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return Result<string, AgentError>.Success(value);
        }

        var result = new StringBuilder(value.Length);
        var at = 0;
        while (at < value.Length)
        {
            var open = value.IndexOf("${", at, StringComparison.Ordinal);
            var close = open < 0 ? -1 : value.IndexOf('}', open + 2);
            if (close < 0)
            {
                result.Append(value, at, value.Length - at);
                break;
            }

            result.Append(value, at, open - at);
            switch (value.AsSpan(open + 2, close - open - 2))
            {
                case "run.id":
                    result.Append(workspace.RunId.ToString("D", CultureInfo.InvariantCulture));
                    break;
                case "run.workspace.root" when CmdWouldExpand(workspace.Root):
                    return Result<string, AgentError>.Failure(Unsafe(server, workspace, "root"));
                case "run.workspace.root":
                    result.Append(workspace.Root);
                    break;
                case "run.workspace.solution" when workspace.SolutionPath is null:
                    return Result<string, AgentError>.Failure(AgentError.ProviderError(
                        $"Run-scoped MCP server '{server}' cannot start for run {workspace.RunId}: the run workspace has no solution."));
                case "run.workspace.solution" when CmdWouldExpand(workspace.SolutionPath):
                    return Result<string, AgentError>.Failure(Unsafe(server, workspace, "solution path"));
                case "run.workspace.solution":
                    result.Append(workspace.SolutionPath);
                    break;
                default:
                    result.Append(value, open, close - open + 1);
                    break;
            }

            at = close + 1;
        }

        return Result<string, AgentError>.Success(result.ToString());
    }

    /// <summary>
    /// On Windows the MCP SDK, ModelContextProtocol.Core 2.2.0 and the newest release, starts every stdio server as
    /// <c>cmd.exe /c</c> and escapes only <c>&amp; ^ &lt; &gt; |</c>, so cmd expands a <c>%NAME%</c> inside an argument.
    /// A substituted value containing <c>%</c> therefore fails the start rather than reach the server changed.
    /// </summary>
    private static bool CmdWouldExpand(string value) => OperatingSystem.IsWindows() && value.Contains('%', StringComparison.Ordinal);

    private static AgentError Unsafe(string server, RunWorkspace workspace, string what) => AgentError.ProviderError(
        $"Run-scoped MCP server '{server}' cannot start for run {workspace.RunId}: the workspace {what} contains '%', which cmd.exe would expand on Windows.");

    // ---------- reloading ----------

    /// <summary>
    /// Applies a pending reload exclusively: called under <see cref="Entry.Gate"/>, so no new lease is handed out, it
    /// waits until every outstanding lease is disposed, then reloads. Removal cancels the wait.
    /// </summary>
    private async ValueTask<Result<McpClient, AgentError>> ReloadAsync(Entry entry, McpClient client, int target, CancellationToken ct)
    {
        // Recorded before the reload runs: a change reported while it is in flight raises Changes past target,
        // so the next call reloads again instead of losing it.
        var previous = entry.AppliedChanges;
        entry.AppliedChanges = target;
        using (var drain = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stopping.Token))
        {
            try
            {
                await entry.WaitForNoLeasesAsync(drain.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                entry.AppliedChanges = previous;
                ct.ThrowIfCancellationRequested();
                return Result<McpClient, AgentError>.Failure(NotRunning(entry.Spec.Name, entry.Workspace.RunId)); // the run is being removed
            }
        }

        LogReloading(_logger, entry.Spec.Name, entry.Workspace.RunId, entry.Spec.Definition.RunScoped!.Reload);
        return entry.Spec.Reload == ReloadKind.Restart
            ? await RestartAsync(entry, client, ct).ConfigureAwait(false)
            : await CallReloadToolAsync(entry, client, previous, ct).ConfigureAwait(false);
    }

    private async ValueTask<Result<McpClient, AgentError>> RestartAsync(Entry entry, McpClient current, CancellationToken ct)
    {
        await DisposeClientAsync(entry, current).ConfigureAwait(false);
        entry.Starting = StartAsync(entry);
        return await entry.Starting.WaitAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<Result<McpClient, AgentError>> CallReloadToolAsync(Entry entry, McpClient client, int previous, CancellationToken ct)
    {
        var tool = entry.Spec.ReloadTool!;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stopping.Token);
        try
        {
            var result = await client.CallToolAsync(tool, arguments: null, progress: null, options: null, call.Token).ConfigureAwait(false);
            if (result.IsError != true)
            {
                return Result<McpClient, AgentError>.Success(client);
            }

            LogReloadFailed(_logger, null, entry.Spec.Name, entry.Workspace.RunId, $"tool '{tool}' reported an error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            entry.AppliedChanges = previous;
            throw;
        }
        catch (Exception ex)
        {
            LogReloadFailed(_logger, ex, entry.Spec.Name, entry.Workspace.RunId, ex.Message);
        }

        entry.AppliedChanges = previous; // not applied: the next call retries it
        return Result<McpClient, AgentError>.Failure(AgentError.ProviderError(
            $"Run-scoped MCP server '{entry.Spec.Name}' for run {entry.Workspace.RunId} could not reload with tool '{tool}'."));
    }

    // ---------- stopping ----------

    private Task StopAllAsync(Entry[] entries) => Task.WhenAll(Array.ConvertAll(entries, StopAsync));

    private async Task StopAsync(Entry entry)
    {
        await entry.Stopping.CancelAsync().ConfigureAwait(false);
        await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            entry.Removed = true;
            // Every start observes Stopping, so this completes promptly; a start disposes its own client when stopped.
            var started = await entry.Starting.ConfigureAwait(false);
            if (started.IsSuccess)
            {
                await DisposeClientAsync(entry, started.Value).ConfigureAwait(false);
            }
        }
        finally
        {
            entry.Gate.Release();
        }

        // Safe: Removed is set under the lock, so no later caller starts, reloads or links to Stopping.
        entry.Stopping.Dispose();
    }

    private async ValueTask DisposeClientAsync(Entry entry, McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisposeFailed(_logger, ex, entry.Spec.Name, entry.Workspace.RunId);
        }
    }

    // ---------- helpers ----------

    /// <summary>How many reloads are waiting, right now, for outstanding leases to be disposed, across every run.</summary>
    internal int ReloadsWaitingForLeases
    {
        get
        {
            lock (_sync)
            {
                return _runs.Values.Sum(run => run.Sum(entry => Volatile.Read(ref entry.WaitingForLeases)));
            }
        }
    }

    /// <summary>How many <see cref="WaitAllReadyAsync"/> lookups, and so removal marks, the registry is tracking; zero once none is in flight.</summary>
    internal int InFlightLookupCount
    {
        get
        {
            lock (_sync)
            {
                return _lookups.Values.Sum(inFlight => inFlight.Count);
            }
        }
    }

    private Entry[]? TryGetRun(Guid runId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _runs.GetValueOrDefault(runId);
        }
    }

    private static AgentError NoProvider(Guid runId) =>
        AgentError.ProviderError($"Run {runId} has no run-scoped MCP server: no run workspace provider is registered.");

    private static AgentError NotRunning(string serverName, Guid runId) =>
        AgentError.ProviderError($"Run-scoped MCP server '{serverName}' is not running for run {runId}.");

    private static ServerSpec[] Validate(IReadOnlyDictionary<string, McpServerDefinition> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var specs = new List<ServerSpec>(servers.Count);
        foreach (var (name, definition) in servers)
        {
            ToolSourceName.ThrowIfInvalid(name, nameof(servers));
            if (definition?.RunScoped is not { } runScoped)
            {
                throw new ArgumentException($"MCP server '{name}' has no runScoped section.", nameof(servers));
            }

            if (!string.Equals(definition.EffectiveType, "stdio", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(definition.Command))
            {
                throw new ArgumentException($"Run-scoped MCP server '{name}' must be a stdio server with a command.", nameof(servers));
            }

            var (reload, reloadTool) = runScoped.Reload switch
            {
                "none" => (ReloadKind.None, null),
                "restart" => (ReloadKind.Restart, null),
                { } tool when tool.StartsWith(ToolReloadPrefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(tool[ToolReloadPrefix.Length..]) =>
                    (ReloadKind.Tool, tool[ToolReloadPrefix.Length..]),
                _ => throw new ArgumentException(
                    $"Run-scoped MCP server '{name}' has reload '{runScoped.Reload}'; use \"none\", \"tool:<name>\" or \"restart\".", nameof(servers)),
            };
            if (runScoped.ReadyTool is { } readyTool && string.IsNullOrWhiteSpace(readyTool))
            {
                throw new ArgumentException($"Run-scoped MCP server '{name}' has a blank readyTool.", nameof(servers));
            }

            specs.Add(new ServerSpec(name, definition, reload, reloadTool));
        }

        return [.. specs.OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    private enum ReloadKind
    {
        None,
        Tool,
        Restart,
    }

    private sealed record ServerSpec(string Name, McpServerDefinition Definition, ReloadKind Reload, string? ReloadTool);

    /// <summary>A <see cref="WaitAllReadyAsync"/> lookup in flight; <see cref="Removed"/> is set, under the registry lock, when its run is removed.</summary>
    private sealed class Lookup
    {
        public bool Removed { get; set; }
    }

    /// <summary>One server of one run. <see cref="Gate"/> serialises every start, reload, lease hand-out and stop of it.</summary>
    private sealed class Entry(ServerSpec spec, RunWorkspace workspace)
    {
        /// <summary>Incremented, without a lock, for every change notification.</summary>
        public int Changes;

        /// <summary>Outstanding leases: incremented under <see cref="Gate"/>, decremented by <see cref="ReleaseLease"/> without it.</summary>
        public int Leases;

        /// <summary>1 while a reload is blocked in <see cref="WaitForNoLeasesAsync"/>; read by <see cref="ReloadsWaitingForLeases"/>.</summary>
        public int WaitingForLeases;

        private TaskCompletionSource? _drained;

        public ServerSpec Spec { get; } = spec;

        public RunWorkspace Workspace { get; } = workspace;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Cancelled when the run's servers are stopped; every start, poll and reload observes it.</summary>
        public CancellationTokenSource Stopping { get; } = new();

        /// <summary>The current start; replaced only under <see cref="Gate"/>, or before the entry is published.</summary>
        public Task<Result<McpClient, AgentError>> Starting { get; set; } = Task.FromResult(Result<McpClient, AgentError>.Failure(AgentError.ProviderError("not started")));

        /// <summary>The <see cref="Changes"/> count the server last reloaded for; read and written under <see cref="Gate"/>.</summary>
        public int AppliedChanges { get; set; }

        /// <summary>Set under <see cref="Gate"/> once the run's servers are stopped; nothing starts or reloads it afterwards.</summary>
        public bool Removed { get; set; }

        /// <summary>Called once per lease by <see cref="RunMcpClientLease.DisposeAsync"/>; wakes a reload waiting for the last one.</summary>
        public void ReleaseLease()
        {
            if (Interlocked.Decrement(ref Leases) == 0)
            {
                Volatile.Read(ref _drained)?.TrySetResult();
            }
        }

        /// <summary>Waits until no lease is outstanding. Called under <see cref="Gate"/>, so the count only falls meanwhile.</summary>
        public async Task WaitForNoLeasesAsync(CancellationToken ct)
        {
            while (Volatile.Read(ref Leases) != 0)
            {
                var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _drained, drained);
                if (Volatile.Read(ref Leases) == 0)
                {
                    break; // the last lease went between the check and the publish
                }

                Interlocked.Increment(ref WaitingForLeases);
                try
                {
                    await drained.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref WaitingForLeases);
                }
            }

            Interlocked.Exchange(ref _drained, null);
        }
    }

    [LoggerMessage(EventId = 310, Level = LogLevel.Information, Message = "Starting run-scoped MCP server '{Server}' for run {RunId}")]
    private static partial void LogStarting(ILogger logger, string server, Guid runId);

    [LoggerMessage(EventId = 311, Level = LogLevel.Information, Message = "Run-scoped MCP server '{Server}' for run {RunId} is ready")]
    private static partial void LogReady(ILogger logger, string server, Guid runId);

    [LoggerMessage(EventId = 312, Level = LogLevel.Warning, Message = "Run-scoped MCP server '{Server}' for run {RunId} did not start: {Error}")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string server, Guid runId, string error);

    [LoggerMessage(EventId = 313, Level = LogLevel.Warning, Message = "Run-scoped MCP server '{Server}' for run {RunId} cannot start: {Error}")]
    private static partial void LogStartRefused(ILogger logger, string server, Guid runId, string error);

    [LoggerMessage(EventId = 314, Level = LogLevel.Information, Message = "Reloading run-scoped MCP server '{Server}' for run {RunId} ({Reload})")]
    private static partial void LogReloading(ILogger logger, string server, Guid runId, string reload);

    [LoggerMessage(EventId = 315, Level = LogLevel.Warning, Message = "Reloading run-scoped MCP server '{Server}' for run {RunId} failed: {Error}")]
    private static partial void LogReloadFailed(ILogger logger, Exception? exception, string server, Guid runId, string error);

    [LoggerMessage(EventId = 316, Level = LogLevel.Warning, Message = "Disposing run-scoped MCP server '{Server}' for run {RunId} failed")]
    private static partial void LogDisposeFailed(ILogger logger, Exception exception, string server, Guid runId);

    [LoggerMessage(EventId = 318, Level = LogLevel.Debug, Message = "Run-scoped MCP server '{Server}' for run {RunId} was stopped while starting")]
    private static partial void LogStartStopped(ILogger logger, Exception exception, string server, Guid runId);

    [LoggerMessage(EventId = 317, Level = LogLevel.Debug, Message = "The workspace of run {RunId} is ready after the registry was disposed; no server started")]
    private static partial void LogReadyAfterDispose(ILogger logger, Guid runId);
}
