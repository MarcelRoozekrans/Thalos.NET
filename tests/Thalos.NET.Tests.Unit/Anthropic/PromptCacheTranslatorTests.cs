using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions.Execution;
using global::Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Anthropic;
using Thalos.Caching;

namespace Thalos.Tests.Unit.Anthropic;

/// <summary>
/// <see cref="AnthropicPromptCacheTranslator"/> through the real provider and the real SDK client, over a stub HTTP
/// handler that records each request's JSON, with <see cref="PromptCachingChatClient"/> placing the hints on top.
/// Multi-assertion tests run in an <see cref="AssertionScope"/>, so every assertion a change breaks is reported.
/// </summary>
public sealed class PromptCacheTranslatorTests
{
    private const string Usage = "\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":20,\"cache_read_input_tokens\":30,\"output_tokens\":5}";

    private static (IChatClient Client, RecordingHandler Handler) Provider(bool enabled, string ttl = "5m", bool hints = true)
    {
        var handler = new RecordingHandler();
        var options = new AnthropicOptions { ApiKey = "sk-test", DefaultModel = "claude-test" };
        options.PromptCaching.Enabled = enabled;
        options.PromptCaching.Ttl = ttl;
        var provider = new AnthropicChatClientProvider(Options.Create(options), new HttpClient(handler));
        var client = provider.CreateChatClient(new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" });
        return (hints ? new PromptCachingChatClient(client) : client, handler);
    }

    private static ChatOptions HintedOptions() => new() { Instructions = "sys", Tools = [Fn("t1"), Fn("t2")] };

    private static AIFunction Fn(string name) => AIFunctionFactory.Create(() => name, name);

    private static ChatMessage Hinted(ChatMessage message)
    {
        message.AdditionalProperties = new() { [PromptCacheHints.Breakpoint] = true };
        return message;
    }

    private static ChatMessage Transient(ChatMessage message)
    {
        message.AdditionalProperties = new() { [PromptCacheHints.Transient] = true };
        return message;
    }

    private static JsonElement Body(RecordingHandler handler) => JsonDocument.Parse(handler.LastBody).RootElement;

    private static int CountCacheControl(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Sum(p => (p.NameEquals("cache_control") ? 1 : 0) + CountCacheControl(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Sum(CountCacheControl),
        _ => 0,
    };

    private static bool HasCacheControl(JsonElement block) => block.TryGetProperty("cache_control", out _);

    /// <summary>The block's <c>cache_control.ttl</c>, or null when it has none.</summary>
    private static string? Ttl(JsonElement block) =>
        block.TryGetProperty("cache_control", out var control) && control.TryGetProperty("ttl", out var ttl) ? ttl.GetString() : null;

    private static JsonElement LastTool(JsonElement body)
    {
        var tools = body.GetProperty("tools");   // JsonElement has no Index indexer, so no [^1] (ruling R25)
        return tools[tools.GetArrayLength() - 1];
    }

    /// <summary>Indices of the request's <c>messages</c> that carry a marker on any content block.</summary>
    private static List<int> MarkedMessages(JsonElement body) =>
        [.. body.GetProperty("messages").EnumerateArray()
            .Select((message, index) => (Content: message.GetProperty("content"), Index: index))
            .Where(m => m.Content.ValueKind == JsonValueKind.Array && m.Content.EnumerateArray().Any(HasCacheControl))
            .Select(m => m.Index)];

    private static JsonElement LastBlock(JsonElement body, int message)
    {
        var content = body.GetProperty("messages")[message].GetProperty("content");
        return content[content.GetArrayLength() - 1];
    }

    // ---------- placement ----------

    [Fact]
    public async Task Three_cache_control_markers_are_emitted_for_the_three_hints()
    {
        var (client, handler) = Provider(enabled: true);
        await client.GetResponseAsync([new(ChatRole.User, "q")], HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(3);
        Ttl(LastTool(body)).Should().Be("5m");
        HasCacheControl(body.GetProperty("system")[0]).Should().BeTrue();
        MarkedMessages(body).Should().Equal(0);
    }

    [Fact]
    public async Task Only_the_latest_message_of_a_longer_history_is_marked()
    {
        var (client, handler) = Provider(enabled: true);
        await client.GetResponseAsync([new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2")], HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(3);
        MarkedMessages(body).Should().Equal(2);
    }

    [Fact]
    public async Task The_message_before_the_first_transient_message_gets_the_fourth_marker()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages = [new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), Transient(new(ChatRole.User, "memories")), new(ChatRole.User, "q2")];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Caller_hints_beyond_the_standard_four_are_dropped()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages =
        [
            Hinted(new(ChatRole.User, "m0")), Hinted(new(ChatRole.Assistant, "m1")), Hinted(new(ChatRole.User, "m2")),
            new(ChatRole.Assistant, "m3"), Transient(new(ChatRole.User, "memories")), new(ChatRole.User, "q"),
        ];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        HasCacheControl(LastTool(body)).Should().BeTrue();
        HasCacheControl(body.GetProperty("system")[0]).Should().BeTrue();
        MarkedMessages(body).Should().Equal(3, 5);
    }

    [Fact]
    public async Task Caller_hints_fill_free_slots_in_list_order()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages =
        [
            Hinted(new(ChatRole.User, "m0")), Hinted(new(ChatRole.Assistant, "m1")), Hinted(new(ChatRole.User, "m2")), new(ChatRole.User, "q"),
        ];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().Equal(0, 3);
    }

    [Fact]
    public async Task Markers_the_caller_set_through_the_sdk_count_toward_the_limit()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages =
        [
            new(ChatRole.User, [new TextContent("native").WithCacheControl(new CacheControlEphemeral())]),
            new(ChatRole.Assistant, "a1"), Transient(new(ChatRole.User, "memories")), new(ChatRole.User, "q"),
        ];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().Equal(0, 3);
    }

    /// <summary>A history whose standard hints are four: tool, instructions, latest message and the one before the transient message.</summary>
    private static List<ChatMessage> FourHintHistory(ChatMessage first) =>
        [first, new(ChatRole.Assistant, "a1"), Transient(new(ChatRole.User, "memories")), new(ChatRole.User, "q")];

    [Fact]
    public async Task A_marker_inside_a_raw_content_block_counts_toward_the_limit()
    {
        var (client, handler) = Provider(enabled: true);
        var raw = new TextContent("raw") { RawRepresentation = (ContentBlockParam)new TextBlockParam { Text = "raw", CacheControl = new CacheControlEphemeral() } };

        await client.GetResponseAsync(FourHintHistory(new(ChatRole.User, [raw])), HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().Equal(0, 3);
    }

    [Fact]
    public async Task A_marker_inside_a_raw_system_text_block_counts_toward_the_limit()
    {
        var (client, handler) = Provider(enabled: true);
        var raw = new TextContent("s") { RawRepresentation = new TextBlockParam { Text = "s", CacheControl = new CacheControlEphemeral() } };
        List<ChatMessage> messages = [new(ChatRole.System, [raw]), .. FourHintHistory(new(ChatRole.User, "q1"))];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().Equal(3);
    }

    [Fact]
    public async Task A_marker_on_a_raw_sdk_tool_counts_toward_the_limit()
    {
        var (client, handler) = Provider(enabled: true);
        var schema = new InputSchema(new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["type"] = JsonSerializer.SerializeToElement("object") });
        var rawTool = ((ToolUnion)new Tool { Name = "raw", InputSchema = schema, CacheControl = new CacheControlEphemeral() }).AsAITool();
        var options = HintedOptions();
        options.Tools!.Insert(0, rawTool);

        await client.GetResponseAsync(FourHintHistory(new(ChatRole.User, "q1")), options, CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().Equal(3);
    }

    [Fact]
    public async Task Markers_in_the_raw_request_from_the_representation_factory_count_toward_the_limit()
    {
        var (client, handler) = Provider(enabled: true);
        var options = HintedOptions();
        options.RawRepresentationFactory = _ => new MessageCreateParams
        {
            MaxTokens = 100,
            Model = "claude-test",
            Messages = [],
            System = new List<TextBlockParam> { new() { Text = "raw system", CacheControl = new CacheControlEphemeral() } },
            CacheControl = new CacheControlEphemeral(),
        };

        await client.GetResponseAsync(FourHintHistory(new(ChatRole.User, "q1")), options, CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(4);
        MarkedMessages(body).Should().BeEmpty();
    }

    [Fact]
    public async Task The_representation_factory_runs_once_per_request()
    {
        // No PromptCachingChatClient, so the translator gets the caller's own options object.
        var (client, _) = Provider(enabled: true, hints: false);
        var calls = 0;
        Func<IChatClient, object?> factory = _ =>
        {
            calls++;
            return new MessageCreateParams { MaxTokens = 100, Model = "claude-test", Messages = [] };
        };
        var options = HintedOptions();
        options.RawRepresentationFactory = factory;

        await client.GetResponseAsync(FourHintHistory(new(ChatRole.User, "q1")), options, CancellationToken.None);
        var afterResponse = calls;
        await foreach (var update in client.GetStreamingResponseAsync(FourHintHistory(new(ChatRole.User, "q1")), options, CancellationToken.None))
        {
            _ = update;
        }

        using var scope = new AssertionScope();
        afterResponse.Should().Be(1);
        (calls - afterResponse).Should().Be(1);
        options.RawRepresentationFactory.Should().BeSameAs(factory);
    }

    [Fact]
    public async Task A_raw_text_block_in_a_user_message_is_mapped_and_so_can_carry_the_marker()
    {
        var (client, handler) = Provider(enabled: true);
        var mapped = new TextContent("b") { RawRepresentation = new TextBlockParam { Text = "b" } };

        await client.GetResponseAsync([new(ChatRole.User, [new TextContent("a"), mapped])], HintedOptions(), CancellationToken.None);

        var content = Body(handler).GetProperty("messages")[0].GetProperty("content");
        using var scope = new AssertionScope();
        HasCacheControl(content[0]).Should().BeFalse();
        HasCacheControl(content[1]).Should().BeTrue();
    }

    [Fact]
    public async Task Blank_text_in_a_hinted_system_message_is_sent_and_so_carries_the_marker()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages = [Hinted(new(ChatRole.System, [new TextContent("s1"), new TextContent(" ")])), new(ChatRole.User, "q")];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var system = Body(handler).GetProperty("system");
        using var scope = new AssertionScope();
        HasCacheControl(system[0]).Should().BeFalse();
        HasCacheControl(system[1]).Should().BeTrue();
    }

    [Fact]
    public async Task A_raw_text_block_in_a_hinted_system_message_is_sent_verbatim_and_so_is_skipped()
    {
        var (client, handler) = Provider(enabled: true);
        var verbatim = new TextContent("s2") { RawRepresentation = new TextBlockParam { Text = "s2" } };
        List<ChatMessage> messages = [Hinted(new(ChatRole.System, [new TextContent("s1"), verbatim])), new(ChatRole.User, "q")];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var system = Body(handler).GetProperty("system");
        using var scope = new AssertionScope();
        HasCacheControl(system[0]).Should().BeTrue();
        HasCacheControl(system[1]).Should().BeFalse();
    }

    [Fact]
    public async Task A_tool_result_as_the_latest_message_is_marked_on_its_tool_result_block()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "t1", new Dictionary<string, object?>(StringComparer.Ordinal))]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]),
        ];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(3);
        HasCacheControl(LastBlock(body, 2)).Should().BeTrue();
    }

    [Fact]
    public async Task A_function_call_as_the_latest_message_is_marked_on_its_tool_use_block()
    {
        var (client, handler) = Provider(enabled: true);
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "t1", new Dictionary<string, object?>(StringComparer.Ordinal))]),
        ];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        HasCacheControl(LastBlock(Body(handler), 1)).Should().BeTrue();
    }

    [Fact]
    public async Task Blank_text_and_raw_sdk_blocks_are_skipped_for_the_last_sendable_content()
    {
        var (client, handler) = Provider(enabled: true);
        var raw = new TextContent("raw") { RawRepresentation = (ContentBlockParam)new TextBlockParam { Text = "raw" } };
        List<ChatMessage> messages = [new(ChatRole.User, [new TextContent("q"), raw, new TextContent("  ")])];

        await client.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        CountCacheControl(body).Should().Be(3);
        HasCacheControl(body.GetProperty("messages")[0].GetProperty("content")[0]).Should().BeTrue();
    }

    [Fact]
    public async Task Instructions_follow_a_leading_caller_system_message_and_carry_the_marker()
    {
        var (client, handler) = Provider(enabled: true);
        await client.GetResponseAsync([new(ChatRole.System, "caller system"), new(ChatRole.User, "q")], HintedOptions(), CancellationToken.None);

        var system = Body(handler).GetProperty("system");
        using var scope = new AssertionScope();
        system.GetArrayLength().Should().Be(2);
        system[0].GetProperty("text").GetString().Should().Be("caller system");
        HasCacheControl(system[0]).Should().BeFalse();
        system[1].GetProperty("text").GetString().Should().Be("sys");
        HasCacheControl(system[1]).Should().BeTrue();
    }

    [Fact]
    public async Task A_one_hour_ttl_is_sent_on_every_marker()
    {
        var (client, handler) = Provider(enabled: true, ttl: "1h");
        await client.GetResponseAsync([new(ChatRole.User, "q")], HintedOptions(), CancellationToken.None);

        var body = Body(handler);
        using var scope = new AssertionScope();
        Ttl(LastTool(body)).Should().Be("1h");
        Ttl(body.GetProperty("system")[0]).Should().Be("1h");
        Ttl(LastBlock(body, 0)).Should().Be("1h");
    }

    [Fact]
    public async Task No_markers_when_caching_is_off()
    {
        var (client, handler) = Provider(enabled: false);
        await client.GetResponseAsync([new(ChatRole.User, "q")], HintedOptions(), CancellationToken.None);
        CountCacheControl(Body(handler)).Should().Be(0);
    }

    [Fact]
    public async Task A_request_without_hints_is_sent_exactly_as_with_caching_off()
    {
        var (on, onHandler) = Provider(enabled: true, hints: false);
        var (off, offHandler) = Provider(enabled: false, hints: false);
        List<ChatMessage> messages = [new(ChatRole.System, "caller system"), new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2")];

        await on.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);
        await off.GetResponseAsync(messages, HintedOptions(), CancellationToken.None);

        onHandler.LastBody.Should().Be(offHandler.LastBody);
    }

    // ---------- caller objects ----------

    [Fact]
    public async Task Caller_messages_and_options_are_never_mutated()
    {
        // Every hint is caller-placed and no PromptCachingChatClient copies anything, so the translator sees the caller's objects.
        var (client, handler) = Provider(enabled: true, hints: false);
        var hintedTool = AIFunctionFactory.Create(() => 2, new AIFunctionFactoryOptions
        {
            Name = "t2",
            AdditionalProperties = new Dictionary<string, object?>(StringComparer.Ordinal) { [PromptCacheHints.Breakpoint] = true },
        });
        var options = new ChatOptions
        {
            Instructions = "sys",
            Tools = [Fn("t1"), hintedTool],
            AdditionalProperties = new() { [PromptCacheHints.InstructionsBreakpoint] = true },
        };
        var tools = options.Tools.ToList();
        List<ChatMessage> history = [Hinted(new(ChatRole.User, "q0")), Hinted(new(ChatRole.User, "q1"))];
        var callerHinted = history[1].Contents[0];

        await client.GetResponseAsync(history, options, CancellationToken.None);
        history.Add(new(ChatRole.Assistant, "a1"));
        history.Add(Hinted(new(ChatRole.User, "q2")));
        await client.GetResponseAsync(history, options, CancellationToken.None);

        using var scope = new AssertionScope();
        MarkedMessages(Body(handler)).Should().Equal(0, 3);
        history[1].Contents[0].Should().BeSameAs(callerHinted);
        history.SelectMany(m => m.Contents).Should().OnlyContain(c => c.AdditionalProperties == null);
        options.Instructions.Should().Be("sys");
        options.Tools.Should().Equal(tools);
    }

    // ---------- usage ----------

    [Fact]
    public async Task Usage_reports_the_total_input_and_the_cache_split()
    {
        var (client, _) = Provider(enabled: true);
        var usage = (await client.GetResponseAsync([new(ChatRole.User, "q")], cancellationToken: CancellationToken.None)).Usage!;

        using var scope = new AssertionScope();
        usage.InputTokenCount.Should().Be(60);
        usage.CachedInputTokenCount.Should().Be(30);
        usage.AdditionalCounts.Should().ContainKey(TurnUsage.CacheWriteCountKey).WhoseValue.Should().Be(20);
    }

    [Fact]
    public async Task Usage_reports_cache_writes_when_caching_is_off()
    {
        var (client, _) = Provider(enabled: false);
        var usage = (await client.GetResponseAsync([new(ChatRole.User, "q")], cancellationToken: CancellationToken.None)).Usage!;
        usage.AdditionalCounts.Should().ContainKey(TurnUsage.CacheWriteCountKey).WhoseValue.Should().Be(20);
    }

    [Fact]
    public async Task Streamed_usage_reports_the_total_input_and_the_cache_split()
    {
        var (client, _) = Provider(enabled: true);
        var usages = new List<UsageDetails>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "q")], cancellationToken: CancellationToken.None))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent)
                {
                    usages.Add(usageContent.Details);
                }
            }
        }

        usages.Should().ContainSingle();
        using var scope = new AssertionScope();
        usages[0].InputTokenCount.Should().Be(60);
        usages[0].CachedInputTokenCount.Should().Be(30);
        usages[0].AdditionalCounts.Should().ContainKey(TurnUsage.CacheWriteCountKey).WhoseValue.Should().Be(20);
    }

    // ---------- options ----------

    [Fact]
    public void Prompt_caching_is_on_with_a_five_minute_ttl_by_default_and_binds_from_configuration()
    {
        var defaults = new AnthropicOptions().PromptCaching;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Anthropic:ApiKey"] = "sk-test",
            ["Thalos:Anthropic:PromptCaching:Enabled"] = "false",
            ["Thalos:Anthropic:PromptCaching:Ttl"] = "1h",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseAnthropic(configuration).UseInMemorySessionStore());
        using var sp = services.BuildServiceProvider();
        var bound = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.PromptCaching;

        using var scope = new AssertionScope();
        defaults.Enabled.Should().BeTrue();
        defaults.Ttl.Should().Be("5m");
        bound.Enabled.Should().BeFalse();
        bound.Ttl.Should().Be("1h");
    }

    [Fact]
    public void An_unsupported_ttl_fails_options_validation()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseAnthropic(o => { o.ApiKey = "sk-test"; o.PromptCaching.Ttl = "2h"; }).UseInMemorySessionStore());
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*PromptCaching:Ttl*2h*");
    }

    [Fact]
    public void An_unsupported_ttl_is_refused_by_a_directly_constructed_provider()
    {
        var options = new AnthropicOptions { ApiKey = "sk-test" };
        options.PromptCaching.Ttl = "5M";
        var act = () => new AnthropicChatClientProvider(Options.Create(options));
        act.Should().Throw<ArgumentException>().WithMessage("*PromptCaching:Ttl*5M*");
    }

    /// <summary>Records each request body and answers with a fixed message, as JSON or as a server-sent event stream.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private const string Message =
            "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-test\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null," + Usage + "}";

        private const string Stream =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-test\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null," + Usage + "}}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null}," + Usage + "}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var streaming = JsonDocument.Parse(LastBody).RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = streaming
                    ? new StringContent(Stream, Encoding.UTF8, "text/event-stream")
                    : new StringContent(Message, Encoding.UTF8, "application/json"),
            };
        }
    }
}
