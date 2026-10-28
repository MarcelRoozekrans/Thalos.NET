using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZeroAlloc.Rest.Anthropic.Transcripts;

/// <summary>
/// The requests behind the recorded transcripts. The recorder sends them, and the replay tests send the same ones
/// against the recorded responses, so a request lives here once. The streaming requests are the plain ones: they
/// leave <c>stream</c> unset, and <c>StreamMessageAsync</c> adds <c>"stream":true</c> to the body it sends. A replay
/// that compares bytes with a recorded stream request must serialize <c>request with { Stream = true }</c>. That
/// property is internal; the tests assembly sees it through InternalsVisibleTo.
/// </summary>
public static class TranscriptRequests
{
    /// <summary>The cheapest priced model in Daedalus' ModelPricing, 0.10 and 0.50 per million tokens.</summary>
    public const string Model = "claude-haiku-5-5";

    /// <summary>The recorded transcript names, in the order the recorder takes them.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["text", "stream-text", "tool-use", "tool-result", "stream-tool-use", "cache-write", "cache-read"];

    /// <summary>How many padding lines the first cache attempt uses; about ten thousand tokens.</summary>
    public const int InitialPaddingLines = 400;

    /// <summary>Non-streaming: one word back.</summary>
    public static MessageRequest Text() => User(16, "Reply with the single word pong.");

    /// <summary>Streaming: a short count.</summary>
    public static MessageRequest StreamText() => User(32, "Count from 1 to 5, separated by spaces.");

    /// <summary>Non-streaming: the model should call <c>get_weather</c>.</summary>
    public static MessageRequest ToolUse() => ToolUseRequest();

    /// <summary>The same request as <see cref="ToolUse"/>; the stream captures <c>input_json_delta</c>.</summary>
    public static MessageRequest StreamToolUse() => ToolUseRequest();

    /// <summary>
    /// The tool-use conversation plus the assistant's tool call and a tool result; the model should answer in text.
    /// </summary>
    /// <param name="toolUseId">The id of the tool call in the recorded tool-use response.</param>
    /// <param name="toolName">The name of that tool call.</param>
    /// <param name="toolInput">The input of that tool call.</param>
    public static MessageRequest ToolResult(string toolUseId, string toolName, JsonElement toolInput)
    {
        ArgumentNullException.ThrowIfNull(toolUseId);
        ArgumentNullException.ThrowIfNull(toolName);
        var first = ToolUseRequest();
        return first with
        {
            Messages =
            [
                .. first.Messages,
                new MessageParam(MessageRole.Assistant, [new ToolUseBlockParam(toolUseId, toolName, toolInput)]),
                new MessageParam(MessageRole.User, [new ToolResultBlockParam(toolUseId, "12 degrees and cloudy", null)]),
            ],
        };
    }

    /// <summary>The request that writes the cache: a long system block marked as a five minute breakpoint.</summary>
    /// <param name="paddingLines">How long the padding is. See <see cref="Padding"/>.</param>
    public static MessageRequest CacheWrite(int paddingLines) => new()
    {
        Model = Model,
        MaxTokens = 16,
        System = new[] { new TextBlockParam(Padding(paddingLines), CacheControl.FiveMinutes) },
        Messages = [new MessageParam(MessageRole.User, [new TextBlockParam("Reply with the single word pong.")])],
    };

    /// <summary>The request that reads the cache: identical to <see cref="CacheWrite"/>.</summary>
    /// <param name="paddingLines">The same length as the write used.</param>
    public static MessageRequest CacheRead(int paddingLines) => CacheWrite(paddingLines);

    /// <summary>Deterministic text, about 25 tokens a line, so the same count always gives the same bytes.</summary>
    /// <param name="lines">The number of lines.</param>
    public static string Padding(int lines)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Reference entry {i}: the archive keeps ledger {i * 7 % 101} aligned with shelf {i % 13} of room {i % 17}.\n");
        }

        return text.ToString();
    }

    private static MessageRequest User(int maxTokens, string text) => new()
    {
        Model = Model,
        MaxTokens = maxTokens,
        Messages = [new MessageParam(MessageRole.User, [new TextBlockParam(text)])],
    };

    private static MessageRequest ToolUseRequest()
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""");
        return new MessageRequest
        {
            Model = Model,
            MaxTokens = 128,
            Tools = [new ToolDefinition("get_weather", "Gets the current weather for a location.", schema.RootElement.Clone(), null)],
            Messages = [new MessageParam(MessageRole.User, [new TextBlockParam("What is the weather in Amsterdam? Use the tool.")])],
        };
    }
}
