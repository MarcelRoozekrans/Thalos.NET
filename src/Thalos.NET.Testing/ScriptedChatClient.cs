using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Thalos.Testing;

/// <summary>Deterministic <see cref="IChatClient"/> replaying a script of steps. Not thread-safe (tests are sequential).</summary>
public sealed class ScriptedChatClient : IChatClient
{
    private abstract record Step;
    private sealed record TextStep(string Text, int Input, int Output) : Step;
    private sealed record ToolCallStep(string Name, IDictionary<string, object?> Args, string CallId, int Input, int Output) : Step;
    private sealed record ThrowStep(Exception Exception) : Step;

    private readonly Queue<Step> _script = new();
    private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _requests = [];

    public string ModelId { get; init; } = "scripted-model";

    /// <summary>Every request received, in order (messages are snapshotted).</summary>
    public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests => _requests;

    public ScriptedChatClient ThenText(string text, int input = 1, int output = 1)
    { _script.Enqueue(new TextStep(text, input, output)); return this; }

    public ScriptedChatClient ThenToolCall(string name, object args, string? callId = null, int input = 1, int output = 1)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)) ?? [];
        // JsonElement values → plain values so tests can compare
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
                    _ => je.GetRawText(),
                };
            }
        }
        _script.Enqueue(new ToolCallStep(name, dict, callId ?? $"call-{_script.Count + 1}", input, output));
        return this;
    }

    public ScriptedChatClient ThenThrow(Exception exception)
    { _script.Enqueue(new ThrowStep(exception)); return this; }

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
            ToolCallStep c => Build(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(c.CallId, c.Name, c.Args)]), c.Input, c.Output),
            ThrowStep e => throw e.Exception,
            _ => throw new InvalidOperationException("unknown step"),
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var message = response.Messages[0];

        // Split text into word-sized deltas so streaming consumers are exercised; keep tool calls whole.
        if (message.Contents.All(c => c is TextContent))
        {
            var words = message.Text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                var piece = i == words.Length - 1 ? words[i] : words[i] + " ";
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece) { ResponseId = response.ResponseId, MessageId = message.MessageId, ModelId = ModelId };
            }
        }
        else
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, message.Contents) { ResponseId = response.ResponseId, MessageId = message.MessageId, ModelId = ModelId };
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)]) { ResponseId = response.ResponseId, ModelId = ModelId, FinishReason = ChatFinishReason.Stop };
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

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
