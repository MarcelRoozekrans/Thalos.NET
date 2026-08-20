using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Thalos.Channels;

/// <summary>
/// Hosts every registered <see cref="IChannelSource"/>: reads inbound messages, binds them to agent sessions and
/// renders each turn back through the <see cref="IChannelAdapter"/> whose <c>ChannelId</c> matches. One reader loop
/// per source; messages within a conversation are handled in order.
/// </summary>
public sealed partial class ChannelPump(
    IEnumerable<IChannelSource> sources,
    IEnumerable<IChannelAdapter> adapters,
    IAgentRuntime runtime,
    IAgentCatalog catalog,
    IConversationMap conversations,
    IOptions<ChannelOptions> options,
    TimeProvider clock,
    ILogger<ChannelPump> logger) : BackgroundService
{
    private readonly IReadOnlyList<IChannelSource> _sources = [.. sources];
    private readonly Dictionary<string, IChannelAdapter> _adapters =
        adapters.ToDictionary(a => a.ChannelId, StringComparer.Ordinal);

    private readonly IAgentRuntime _runtime = runtime;
    private readonly IAgentCatalog _catalog = catalog;
    private readonly IConversationMap _conversations = conversations;
    private readonly ChannelOptions _options = options.Value;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<ChannelPump> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        await Task.WhenAll(_sources.Select(s => PumpAsync(s, stoppingToken))).ConfigureAwait(false);
    }

    private async Task PumpAsync(IChannelSource source, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(source.ChannelId, out var adapter))
        {
            LogNoAdapter(_logger, source.ChannelId);
            return;
        }

        try
        {
            await foreach (var message in source.ReadAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(message, adapter, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad message must not end the channel. A dead pump loop is invisible until process
                    // shutdown, and for a single-operator bot that reads as "the agent stopped answering".
                    LogHandleFailed(_logger, source.ChannelId, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            // The source itself died, so this loop is over — but siblings keep pumping. Never fail silently.
            LogSourceFailed(_logger, source.ChannelId, ex);
        }
    }

    private async Task HandleAsync(InboundMessage message, IChannelAdapter adapter, CancellationToken ct)
    {
        // Task 9 replaces this with command dispatch; for now every message is a turn.
        var binding = await ResolveAsync(message, ct).ConfigureAwait(false);
        if (binding is null)
        {
            return;
        }

        await RunTurnAsync(message, binding, adapter, ct).ConfigureAwait(false);
    }

    private async Task<ConversationBinding?> ResolveAsync(InboundMessage message, CancellationToken ct)
    {
        // Task 8 replaces this with the four lifecycle edges.
        var existing = await _conversations.GetAsync(message.ChannelId, message.ConversationId, ct).ConfigureAwait(false);
        if (existing.IsSuccess && existing.Value is { } bound)
        {
            return bound;
        }

        if (ResolveAgent(_options.DefaultAgent) is not { } definition)
        {
            LogUnknownAgent(_logger, _options.DefaultAgent);
            return null;
        }

        var created = await _runtime.CreateSessionAsync(definition.Id, message.Caller, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            LogSessionFailed(_logger, message.ChannelId, created.Error.Code);
            return null;
        }

        var binding = new ConversationBinding(
            message.ChannelId, message.ConversationId, created.Value,
            definition.Id, _clock.GetUtcNow());

        await _conversations.BindAsync(binding, ct).ConfigureAwait(false);
        return binding;
    }

    /// <summary>
    /// Resolves a configured or operator-typed agent NAME to its definition. <c>AgentId</c> is a ULID, so configuration
    /// and chat commands name agents by <see cref="AgentDefinition.Name"/>; the catalogue's own TryGet only indexes by id.
    /// Case-insensitive because the name is typed by a human, often on a phone.
    /// </summary>
    private AgentDefinition? ResolveAgent(string name)
    {
        foreach (var definition in _catalog.Agents)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    private async Task RunTurnAsync(InboundMessage message, ConversationBinding binding, IChannelAdapter adapter, CancellationToken ct)
    {
        var coalescer = new DeltaCoalescer(_options.FlushInterval, _clock);
        var request = new AgentTurnRequest(binding.SessionId, message.Text, message.Caller);

        await foreach (var agentEvent in _runtime.RunTurnStreamingAsync(request, ct).ConfigureAwait(false))
        {
            switch (agentEvent)
            {
                case TextDeltaEvent delta:
                    if (coalescer.TryAppend(delta.Text, out var render) && render is not null)
                    {
                        await adapter.DeliverAsync(binding.SessionId,
                            new TextDeltaEvent(delta.SessionId, delta.TurnId, render), ct).ConfigureAwait(false);
                    }

                    break;

                case ToolCallStartedEvent started:
                    coalescer.SetActivity(started.ToolName);
                    if (coalescer.TryAppend(string.Empty, out var toolRender) && toolRender is not null)
                    {
                        await adapter.DeliverAsync(binding.SessionId,
                            new TextDeltaEvent(started.SessionId, started.TurnId, toolRender), ct).ConfigureAwait(false);
                    }

                    break;

                case ToolCallFinishedEvent:
                    coalescer.SetActivity(null);
                    break;

                default:
                    // Terminal and informational events pass through untouched; the adapter decides how to show them.
                    await adapter.DeliverAsync(binding.SessionId, agentEvent, ct).ConfigureAwait(false);
                    break;
            }
        }

        await _conversations.BindAsync(binding with { LastActivityAt = _clock.GetUtcNow() }, ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "Channels are disabled; the pump is idle")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 602, Level = LogLevel.Error, Message = "Channel {ChannelId} has a source but no adapter; it will not be pumped")]
    private static partial void LogNoAdapter(ILogger logger, string channelId);

    [LoggerMessage(EventId = 603, Level = LogLevel.Error, Message = "Channel {ChannelId} could not create a session: {Code}")]
    private static partial void LogSessionFailed(ILogger logger, string channelId, AgentErrorCode code);

    [LoggerMessage(EventId = 604, Level = LogLevel.Error, Message = "No agent is registered under the name {Name}; check Thalos:Channels:DefaultAgent against the agent catalogue")]
    private static partial void LogUnknownAgent(ILogger logger, string name);

    [LoggerMessage(EventId = 605, Level = LogLevel.Error, Message = "Channel {ChannelId} failed handling a message; the channel keeps reading")]
    private static partial void LogHandleFailed(ILogger logger, string channelId, Exception ex);

    [LoggerMessage(EventId = 606, Level = LogLevel.Critical, Message = "Channel {ChannelId} source loop ended with an error; that channel is no longer reading")]
    private static partial void LogSourceFailed(ILogger logger, string channelId, Exception ex);
}
