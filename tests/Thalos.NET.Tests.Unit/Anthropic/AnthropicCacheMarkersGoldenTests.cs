using System.Text.Json;
using global::Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Thalos.Anthropic;

namespace Thalos.Tests.Unit.Anthropic;

/// <summary>
/// How many caller-set <c>cache_control</c> markers <see cref="AnthropicCacheMarkers.CountCallerMarkers"/> finds inside
/// raw SDK objects, one test per site that serialises such an object, captured before the count stops going through
/// reflection-based JSON. Each test reaches exactly one site. Characterization tests: they pass on today's code.
/// </summary>
public sealed class AnthropicCacheMarkersGoldenTests
{
    private static readonly InputSchema Schema = new(new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["type"] = JsonSerializer.SerializeToElement("object") });

    // Red: in CountCallerMarkers, serialise new { } instead of raw.RawBodyData; the raw request's two markers are
    // no longer counted.
    [Fact]
    public void Markers_in_a_raw_request_body_are_counted_as_before()
    {
        var raw = new MessageCreateParams
        {
            MaxTokens = 100,
            Model = "claude-test",
            Messages = [],
            System = new List<TextBlockParam> { new() { Text = "raw system", CacheControl = new CacheControlEphemeral() }, new() { Text = "unmarked" } },
            CacheControl = new CacheControlEphemeral(),
        };

        AnthropicCacheMarkers.CountCallerMarkers([new ChatMessage(ChatRole.User, "q")], tools: null, raw).Should().Be(2);
    }

    // Red: in CountInSystemContent, serialise new { } instead of the raw TextBlockParam; its marker is no longer counted.
    [Fact]
    public void A_marker_in_a_raw_system_block_is_counted_as_before()
    {
        var system = new ChatMessage(ChatRole.System, [
            new TextContent("s") { RawRepresentation = new TextBlockParam { Text = "s", CacheControl = new CacheControlEphemeral() } },
            new TextContent("t") { RawRepresentation = new TextBlockParam { Text = "t" } },
        ]);

        AnthropicCacheMarkers.CountCallerMarkers([system], tools: null, rawRequest: null).Should().Be(1);
    }

    // Red: in CountInContent, serialise new { } instead of the raw ContentBlockParam; its marker is no longer counted.
    [Fact]
    public void A_marker_in_a_raw_content_block_is_counted_as_before()
    {
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("raw") { RawRepresentation = (ContentBlockParam)new TextBlockParam { Text = "raw", CacheControl = new CacheControlEphemeral() } },
            new TextContent("plain") { RawRepresentation = (ContentBlockParam)new TextBlockParam { Text = "plain" } },
        ]);

        AnthropicCacheMarkers.CountCallerMarkers([user], tools: null, rawRequest: null).Should().Be(1);
    }

    // Red: in CountInTool, serialise new { } instead of the raw ToolUnion; its marker is no longer counted.
    [Fact]
    public void A_marker_on_a_raw_tool_is_counted_as_before()
    {
        AITool[] tools =
        [
            ((ToolUnion)new Tool { Name = "marked", InputSchema = Schema, CacheControl = new CacheControlEphemeral() }).AsAITool(),
            ((ToolUnion)new Tool { Name = "unmarked", InputSchema = Schema }).AsAITool(),
        ];

        AnthropicCacheMarkers.CountCallerMarkers([new ChatMessage(ChatRole.User, "q")], tools, rawRequest: null).Should().Be(1);
    }
}
