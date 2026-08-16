using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Thalos.Testing;

/// <summary>
/// Deterministic <see cref="IChatClient"/> that replays a script of steps — text replies, tool-call requests and
/// thrown exceptions — one step per request, and records every request it receives. Use it wherever a runtime test
/// would otherwise need a model; no network is involved.
/// </summary>
/// <remarks>
/// Not thread-safe: tests drive it sequentially. <see cref="Requests"/> is a shallow snapshot — the message list is
/// copied per request, but the <see cref="ChatMessage"/> instances and <see cref="ChatOptions"/> are the caller's.
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    private abstract record Step;
    private sealed record TextStep(string Text, int Input, int Output) : Step;
    private sealed record ToolCallStep(string Name, IDictionary<string, object?> Args, string CallId, int Input, int Output, string? PrecedingText) : Step;
    private sealed record ThrowStep(Exception Exception) : Step;

    private readonly Queue<Step> _script = new();
    private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _requests = [];
    private int _nextCallId;

    /// <summary>Model id stamped on every response and update; also reported through <see cref="ChatClientMetadata"/>.</summary>
    public string ModelId { get; init; } = "scripted-model";

    /// <summary>Every request received, in order (message lists are snapshotted; instances are shared).</summary>
    public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests => _requests;

    /// <summary>Appends a step that replies with assistant <paramref name="text"/> and the given usage.</summary>
    public ScriptedChatClient ThenText(string text, int input = 1, int output = 1)
    { _script.Enqueue(new TextStep(text, input, output)); return this; }

    /// <summary>
    /// Appends a step that requests a tool call. <paramref name="args"/> is serialized with
    /// <see cref="AIJsonUtilities.DefaultOptions"/> (camelCase); primitive values become CLR primitives, objects and
    /// arrays stay <see cref="JsonElement"/>s. <paramref name="callId"/> defaults to <c>call-1</c>, <c>call-2</c>, …
    /// When <paramref name="precedingText"/> is set, the assistant message contains that text <em>before</em> the tool call
    /// (as real providers do) and the streaming path yields the text deltas first, then the tool-call update.
    /// </summary>
    public ScriptedChatClient ThenToolCall(string name, object args, string? callId = null, int input = 1, int output = 1, string? precedingText = null)
    {
        var json = JsonSerializer.Serialize(args, AIJsonUtilities.DefaultOptions);
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, AIJsonUtilities.DefaultOptions) ?? [];
        // JsonElement primitives → plain values so tests can compare; objects/arrays stay JsonElement (cloned off the document)
        var keys = dict.Keys.ToArray();
        foreach (var k in keys)
        {
            if (dict[k] is JsonElement je)
            {
                dict[k] = je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString(),
                    JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => je.Clone(),
                };
            }
        }
        _script.Enqueue(new ToolCallStep(name, dict, callId ?? $"call-{++_nextCallId}", input, output, precedingText));
        return this;
    }

    /// <summary>Appends a step that throws <paramref name="exception"/> when reached.</summary>
    public ScriptedChatClient ThenThrow(Exception exception)
    { _script.Enqueue(new ThrowStep(exception)); return this; }

    /// <summary>Records the request and replays the next step; throws <see cref="InvalidOperationException"/> when the script is exhausted.</summary>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = messages.ToList();
        _requests.Add((snapshot, options));

        if (_script.Count == 0)
        {
            throw new InvalidOperationException($"ScriptedChatClient script exhausted after {_requests.Count} request(s). Last request: {string.Join(" | ", snapshot.Select(m => $"{m.Role}: {m.Text}"))}");
        }

        var step = _script.Dequeue();
        return Task.FromResult(step switch
        {
            TextStep t => Build(new ChatMessage(ChatRole.Assistant, t.Text), t.Input, t.Output),
            ToolCallStep c => Build(new ChatMessage(ChatRole.Assistant, ToolCallContents(c)), c.Input, c.Output),
            ThrowStep e => throw e.Exception,
            _ => throw new InvalidOperationException("unknown step"),
        });
    }

    /// <summary>
    /// Same as <see cref="GetResponseAsync"/> but streamed: text is split into word-sized deltas (text preceding a tool
    /// call is streamed before it), tool calls are yielded whole, and a final update carries <see cref="UsageContent"/>
    /// with <see cref="ChatFinishReason.Stop"/>.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var message = response.Messages[0];

        // Split text into word-sized deltas so streaming consumers are exercised; keep tool calls whole.
        if (message.Contents.Any(c => c is TextContent))
        {
            var words = message.Text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                var piece = i == words.Length - 1 ? words[i] : words[i] + " ";
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece) { ResponseId = response.ResponseId, MessageId = message.MessageId, ModelId = ModelId };
            }
        }

        var calls = message.Contents.Where(c => c is not TextContent).ToList();
        if (calls.Count > 0)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, calls) { ResponseId = response.ResponseId, MessageId = message.MessageId, ModelId = ModelId };
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)]) { ResponseId = response.ResponseId, ModelId = ModelId, FinishReason = ChatFinishReason.Stop };
    }

    private static List<AIContent> ToolCallContents(ToolCallStep step)
    {
        List<AIContent> contents = [];
        if (!string.IsNullOrEmpty(step.PrecedingText))
        {
            contents.Add(new TextContent(step.PrecedingText));
        }

        contents.Add(new FunctionCallContent(step.CallId, step.Name, step.Args));
        return contents;
    }

    private ChatResponse Build(ChatMessage message, int input, int output)
    {
        message.MessageId = Guid.NewGuid().ToString("N");
        return new ChatResponse(message)
        {
            ResponseId = Guid.NewGuid().ToString("N"),
            ModelId = ModelId,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output },
            FinishReason = message.Contents.Any(c => c is FunctionCallContent) ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
        };
    }

    /// <summary>Returns <see cref="ChatClientMetadata"/> (provider <c>scripted</c>, default model <see cref="ModelId"/>) or this instance; null otherwise.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata("scripted", defaultModelId: ModelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>No-op.</summary>
    public void Dispose() { }
}
