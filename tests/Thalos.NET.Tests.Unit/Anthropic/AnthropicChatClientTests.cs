using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Thalos.Anthropic;
using ZeroAlloc.Rest.Anthropic;

namespace Thalos.Tests.Unit.Anthropic;

/// <summary>
/// <see cref="AnthropicChatClient"/>, the M.E.AI adapter over <see cref="AnthropicClient"/>: the request mapping, read
/// back from the JSON it writes, and the response, stream and usage mapping, over a stub HTTP handler. The
/// <c>*_byte_for_byte</c> tests replay requests recorded against the real API from <c>Transcripts/</c>.
/// </summary>
public sealed class AnthropicChatClientTests
{
    private const string DefaultModel = "claude-test";
    private const int DefaultMaxTokens = 1024;
    private const string RecordedModel = "claude-haiku-5-5";
    private const string RecordedToolUseId = "toolu_01Fx742oFGwVU7nkBE13BFUf";

    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Transcripts");

    // ---- Requests -------------------------------------------------------------------------------------------------

    [Fact]
    public void System_messages_and_instructions_become_system_blocks_in_order()
    {
        var request = Map(
            [new(ChatRole.System, "s1"), new(ChatRole.System, "s2"), new(ChatRole.User, "u")],
            new ChatOptions { Instructions = "i" });

        // Red: add Instructions to the system blocks before the system messages in AnthropicRequestMapper.Map.
        SystemTexts(request).Should().Equal("s1", "s2", "i");
        // Red: also map a system message as a user message; there are then three messages.
        MessagesJson(request).Should().Be("""[{"role":"user","content":[{"type":"text","text":"u"}]}]""");
    }

    [Fact]
    public void A_system_message_after_the_conversation_starts_joins_the_system_prompt_in_order()
    {
        var request = Map(
            [new(ChatRole.System, "s1"), new(ChatRole.User, "u"), new(ChatRole.System, "s2")],
            new ChatOptions { Instructions = "i" });

        // Red: skip system messages once a non-system message was mapped; "s2" is then missing.
        SystemTexts(request).Should().Equal("s1", "s2", "i");
    }

    [Fact]
    public void Without_system_messages_or_instructions_no_system_is_sent()
    {
        var request = Map([new(ChatRole.User, "u")], new ChatOptions { Instructions = null });

        // Red: always set System, to an empty block list when there is nothing; "system":[] is then written.
        Wire(request).TryGetProperty("system", out _).Should().BeFalse();
    }

    [Fact]
    public void Blank_user_text_is_dropped_but_blank_system_text_is_sent()
    {
        var request = Map(
        [
            new(ChatRole.System, "  "),
            new(ChatRole.User, [new TextContent(" "), new TextContent("x")]),
            new(ChatRole.User, [new TextContent("\n")]),
        ]);

        // Red: skip blank text in system messages as well; the system prompt is then gone.
        SystemTexts(request).Should().Equal("  ");
        // Red: send blank user text; the first message then has two blocks and the second message is sent.
        MessagesJson(request).Should().Be("""[{"role":"user","content":[{"type":"text","text":"x"}]}]""");
    }

    [Fact]
    public void Assistant_text_is_trimmed_at_the_end_and_dropped_when_blank()
    {
        var request = Map([new(ChatRole.User, " q "), new(ChatRole.Assistant, " a \n"), new(ChatRole.Assistant, "  ")]);

        // Red: send assistant text untrimmed; " a \n" then reaches the wire. Red: trim user text too; " q " then
        // loses its trailing space.
        MessagesJson(request).Should().Be(
            """[{"role":"user","content":[{"type":"text","text":" q "}]},{"role":"assistant","content":[{"type":"text","text":" a"}]}]""");
    }

    [Fact]
    public void Content_the_adapter_does_not_support_is_dropped()
    {
        var request = Map([new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png"), new TextContent("x")])]);

        // Red: map any other content to a text block of its ToString(); the image then becomes a text block.
        MessagesJson(request).Should().Be("""[{"role":"user","content":[{"type":"text","text":"x"}]}]""");
    }

    [Fact]
    public void No_messages_send_one_zero_width_space_user_message()
    {
        var request = Map([new(ChatRole.System, "s")]);

        // Red: drop the zero-width space message from AnthropicRequestMapper.Map; messages is then empty.
        // The serializer escapes U+200B, so the wire holds the six characters \u200B.
        MessagesJson(request).Should().Be("[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"\\u200B\"}]}]");
    }

    [Fact]
    public void A_tool_message_becomes_a_user_message_of_tool_result_blocks()
    {
        var request = Map([new(ChatRole.Tool, [new FunctionResultContent("c1", "r1"), new FunctionResultContent("c2", "r2")])]);

        // Red: map a tool message to the assistant role; "role" is then "assistant".
        // Red: write IsError = exception is not null, false included; "is_error":false is then written.
        MessagesJson(request).Should().Be(
            """[{"role":"user","content":[{"type":"tool_result","tool_use_id":"c1","content":"r1"},{"type":"tool_result","tool_use_id":"c2","content":"r2"}]}]""");
    }

    [Fact]
    public void A_function_result_with_an_exception_sets_is_error()
    {
        var request = Map([new(ChatRole.Tool, [new FunctionResultContent("c1", "boom") { Exception = new InvalidOperationException("boom") }])]);

        // Red: never set IsError; "is_error":true is then missing.
        MessagesJson(request).Should().Be(
            """[{"role":"user","content":[{"type":"tool_result","tool_use_id":"c1","content":"boom","is_error":true}]}]""");
    }

    [Fact]
    public void A_non_string_tool_result_is_sent_as_its_json()
    {
        using var document = JsonDocument.Parse("""{"a":1}""");
        var request = Map(
        [
            new(ChatRole.Tool,
            [
                new FunctionResultContent("c1", 42),
                new FunctionResultContent("c2", document.RootElement.Clone()),
                new FunctionResultContent("c3", null),
            ]),
        ]);

        // AIJsonUtilities.DefaultOptions writes indented, with Environment.NewLine, as the SDK's tool results were.
        // Red: serialize through a copy of those options with WriteIndented off; the object is then on one line.
        // Red: map a null result to null content; the third entry is then null instead of "null".
        ToolResultContents(request).Should().Equal("42", "{" + Environment.NewLine + "  \"a\": 1" + Environment.NewLine + "}", "null");
    }

    [Fact]
    public void Text_content_tool_results_are_sent_as_their_text()
    {
        var request = Map(
        [
            new(ChatRole.Tool,
            [
                new FunctionResultContent("c1", new TextContent("one")),
                new FunctionResultContent("c2", new AIContent[] { new TextContent("a"), new TextContent("b") }),
            ]),
        ]);

        // Red: serialize an AIContent result as JSON like any other object; "one" then arrives as {"$type":"text",...}.
        // Red: join the texts of a content list with "" instead of a line break; "a\nb" then reads "ab".
        ToolResultContents(request).Should().Equal("one", "a\nb");
    }

    [Fact]
    public void A_function_call_sends_its_arguments_as_tool_input()
    {
        using var nested = JsonDocument.Parse("""{"k":true}""");
        var request = Map(
        [
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "f", new Dictionary<string, object?>(StringComparer.Ordinal) { ["s"] = "x", ["n"] = 2, ["j"] = nested.RootElement.Clone(), ["z"] = null }),
                new FunctionCallContent("c2", "g"),
            ]),
        ]);

        // Red: write a JsonElement argument as a string of its ToString(); "j" is then "{\"k\":true}" in quotes.
        // Red: write null arguments as JSON null; c2 then has "input":null instead of {}.
        MessagesJson(request).Should().Be(
            """[{"role":"assistant","content":[{"type":"tool_use","id":"c1","name":"f","input":{"s":"x","n":2,"j":{"k":true},"z":null}},{"type":"tool_use","id":"c2","name":"g","input":{}}]}]""");
    }

    [Fact]
    public void Options_map_model_max_tokens_temperature_top_p_stop_sequences()
    {
        var set = Map([new(ChatRole.User, "u")], new ChatOptions { ModelId = "m", MaxOutputTokens = 7, Temperature = 0.5f, TopP = 0.25f, StopSequences = ["x", "y"] });
        var unset = Map([new(ChatRole.User, "u")], new ChatOptions { StopSequences = [] });

        // Red: ignore ChatOptions.ModelId; the model is then claude-test.
        set.Model.Should().Be("m");
        // Red: ignore ChatOptions.MaxOutputTokens; max_tokens is then 1024.
        set.MaxTokens.Should().Be(7);
        // Red: drop Temperature from the mapping; it is then null.
        set.Temperature.Should().Be(0.5);
        // Red: map TopP from Temperature; it is then 0.5.
        set.TopP.Should().Be(0.25);
        // Red: drop StopSequences from the mapping; it is then null.
        set.StopSequences.Should().Equal("x", "y");
        // Red: use a fixed model instead of the default one; this then differs.
        unset.Model.Should().Be(DefaultModel);
        // Red: use a fixed 4096 instead of the default max tokens; this then differs.
        unset.MaxTokens.Should().Be(DefaultMaxTokens);
        // Red: send an empty StopSequences list as []; "stop_sequences":[] is then written and the request differs.
        Wire(unset).GetRawText().Should().Be("""{"model":"claude-test","max_tokens":1024,"messages":[{"role":"user","content":[{"type":"text","text":"u"}]}]}""");
    }

    [Fact]
    public void An_AIFunction_becomes_a_tool_with_its_json_schema()
    {
        var function = Declaration("lookup", "Looks a city up.", """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""");
        var request = Map([new(ChatRole.User, "u")], new ChatOptions { Tools = [function] });

        var tool = request.Tools.Should().ContainSingle().Subject;
        // Red: use the function's Description for its name; this then differs.
        tool.Name.Should().Be("lookup");
        // Red: drop the description; this is then null.
        tool.Description.Should().Be("Looks a city up.");
        // Red: send the function's JsonSchema untransformed; "additionalProperties":false is then missing.
        // Red: send an empty object schema instead; the texts then differ.
        tool.InputSchema.GetRawText().Should().Be(
            """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""");
        // Red: always mark a tool with a five minute cache control; this is then not null.
        tool.CacheControl.Should().BeNull();
    }

    [Fact]
    public void A_tool_that_is_not_a_function_is_not_sent()
    {
        var request = Map([new(ChatRole.User, "u")], new ChatOptions { Tools = [new HostedWebSearchTool()] });

        // Red: send Tools as an empty list when no tool maps; "tools":[] is then written.
        Wire(request).TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_text_transcript_request_is_reproduced_byte_for_byte()
    {
        var (client, handler) = Client(() => Json(Read("text.response.json")), RecordedModel);

        var response = await client.GetResponseAsync([new(ChatRole.User, "Reply with the single word pong.")], new ChatOptions { MaxOutputTokens = 16 });

        // Red: always set System, to an empty block list when there is none; "system":[] then joins the bytes.
        handler.Bodies.Should().ContainSingle().Which.Should().Be(Read("text.request.json"));
        // Red: drop TextBlock from the response mapping; the text is then empty.
        response.Text.Should().Be("Pong");
    }

    [Fact]
    public async Task The_tool_result_transcript_request_is_reproduced_byte_for_byte()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""");
        var tool = AIFunctionFactory.CreateDeclaration("get_weather", "Gets the current weather for a location.", schema.RootElement.Clone());
        var (client, handler) = Client(() => Json(Read("tool-result.response.json")), RecordedModel);

        await client.GetResponseAsync(
            [
                new(ChatRole.User, "What is the weather in Amsterdam? Use the tool."),
                new(ChatRole.Assistant, [new FunctionCallContent(RecordedToolUseId, "get_weather", new Dictionary<string, object?>(StringComparer.Ordinal) { ["location"] = "Amsterdam" })]),
                new(ChatRole.Tool, [new FunctionResultContent(RecordedToolUseId, "12 degrees and cloudy")]),
            ],
            new ChatOptions { MaxOutputTokens = 128, Tools = [tool] });

        // The recording was made with the schema untransformed; everything but tools[].input_schema must match it.
        var sent = WithoutInputSchemas(handler.Bodies.Should().ContainSingle().Subject, out var sentSchema);
        var recorded = WithoutInputSchemas(Read("tool-result.request.json"), out _);
        // Red: write IsError = exception is not null, false included; "is_error":false then joins the request.
        // Red: drop the tool description; the requests then differ.
        sent.Should().Be(recorded);
        // Red: send the function's JsonSchema untransformed; "additionalProperties":false is then missing.
        sentSchema.Should().Be(
            """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"],"additionalProperties":false}""");
    }

    [Fact]
    public void An_unsupported_schema_keyword_is_removed_and_appended_to_the_description()
    {
        var schema = ToolSchema(Declaration("f", null,
            """{"type":"object","properties":{"name":{"type":"string","description":"Who.","minLength":2,"format":"email"},"tags":{"type":"array","items":{"type":"string"},"minItems":3}}}"""));

        // Red: keep keywords outside the string whitelist; "minLength" is then still on "name".
        // Red: drop a removed keyword without appending it; the description then reads only "Who.".
        // Red: remove a supported format such as email; "format" is then missing.
        schema["properties"]!["name"]!.ToJsonString().Should().Be(
            """{"type":"string","description":"Who.\n\n{minLength: 2}","format":"email"}""");
        // Red: keep any minItems; "minItems":3 is then still there and no description is added.
        schema["properties"]!["tags"]!.ToJsonString().Should().Be(
            """{"type":"array","items":{"type":"string"},"description":"{minItems: 3}"}""");
    }

    [Fact]
    public void A_oneOf_schema_becomes_anyOf()
    {
        var schema = ToolSchema(Declaration("f", null,
            """{"type":"object","properties":{"v":{"oneOf":[{"type":"string"},{"type":"integer"}]}}}"""));

        // Red: leave oneOf in place; it is then removed as unsupported and the union lands in the description.
        schema["properties"]!["v"]!.ToJsonString().Should().Be("""{"anyOf":[{"type":"string"},{"type":"integer"}]}""");
    }

    [Fact]
    public void A_nested_object_schema_disallows_additional_properties()
    {
        var schema = ToolSchema(Declaration("f", null,
            """{"type":"object","properties":{"address":{"type":"object","properties":{"city":{"type":"string"}}}}}"""));

        // Red: set DisallowAdditionalProperties to false; the nested object then has no "additionalProperties".
        schema["properties"]!["address"]!.ToJsonString().Should().Be(
            """{"type":"object","properties":{"city":{"type":"string"}},"additionalProperties":false}""");
    }

    [Fact]
    public async Task The_stream_text_transcript_request_is_reproduced_byte_for_byte()
    {
        var (client, handler) = Client(() => Sse(Read("stream-text.response.sse")), RecordedModel);

        await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Count from 1 to 5, separated by spaces.")], new ChatOptions { MaxOutputTokens = 32 }));

        // Red: map the streaming request without its options; max_tokens is then 1024 instead of 32.
        handler.Bodies.Should().ContainSingle().Which.Should().Be(Read("stream-text.request.json"));
    }

    [Fact]
    public void A_cache_breakpoint_on_content_and_tool_reaches_the_wire()
    {
        var mark = new AdditionalPropertiesDictionary { [AnthropicChatClient.CacheBreakpointKey] = new CacheBreakpoint(CacheControl.OneHour) };
        var tool = AIFunctionFactory.Create(() => 1, new AIFunctionFactoryOptions
        {
            Name = "t",
            AdditionalProperties = new Dictionary<string, object?>(StringComparer.Ordinal) { [AnthropicChatClient.CacheBreakpointKey] = new CacheBreakpoint(CacheControl.FiveMinutes) },
        });
        var request = Map(
            [
                new(ChatRole.System, [new TextContent("s") { AdditionalProperties = mark }]),
                new(ChatRole.User, [new TextContent("u") { AdditionalProperties = mark }]),
                new(ChatRole.Assistant, [new FunctionCallContent("c1", "t") { AdditionalProperties = mark }]),
                new(ChatRole.Tool, [new FunctionResultContent("c1", "r") { AdditionalProperties = mark }]),
            ],
            new ChatOptions { Tools = [tool] });
        var wire = Wire(request);
        const string OneHour = """{"type":"ephemeral","ttl":"1h"}""";

        // Red: drop the breakpoint from system text blocks; this is then missing.
        CacheControlOf(wire.GetProperty("system")[0]).Should().Be(OneHour);
        // Red: drop the breakpoint from user text blocks; this is then missing.
        CacheControlOf(wire.GetProperty("messages")[0].GetProperty("content")[0]).Should().Be(OneHour);
        // Red: drop the breakpoint from tool_use blocks; this is then missing.
        CacheControlOf(wire.GetProperty("messages")[1].GetProperty("content")[0]).Should().Be(OneHour);
        // Red: drop the breakpoint from tool_result blocks; this is then missing.
        CacheControlOf(wire.GetProperty("messages")[2].GetProperty("content")[0]).Should().Be(OneHour);
        // Red: drop the breakpoint from tool definitions; this is then missing.
        CacheControlOf(wire.GetProperty("tools")[0]).Should().Be("""{"type":"ephemeral","ttl":"5m"}""");
    }

    [Fact]
    public void A_plain_additional_property_does_not()
    {
        var raw = new AdditionalPropertiesDictionary
        {
            [AnthropicChatClient.CacheBreakpointKey] = CacheControl.OneHour,
            ["cache_control"] = new CacheBreakpoint(CacheControl.OneHour),
            ["anthropic:cache_control"] = new CacheBreakpoint(CacheControl.OneHour),
        };
        var tool = AIFunctionFactory.Create(() => 1, new AIFunctionFactoryOptions
        {
            Name = "t",
            AdditionalProperties = new Dictionary<string, object?>(StringComparer.Ordinal) { [AnthropicChatClient.CacheBreakpointKey] = "1h", ["CacheControl"] = CacheControl.OneHour },
        });
        var request = Map(
            [
                new(ChatRole.System, [new TextContent("s") { AdditionalProperties = raw }]),
                new(ChatRole.User, [new TextContent("u") { AdditionalProperties = raw }]),
                new(ChatRole.Assistant, [new FunctionCallContent("c1", "t") { AdditionalProperties = raw }]),
                new(ChatRole.Tool, [new FunctionResultContent("c1", "r") { AdditionalProperties = raw }]),
            ],
            new ChatOptions { Tools = [tool] });

        // Red: accept a bare CacheControl under the breakpoint key; every block is then marked.
        // Red: read the breakpoint from any key whose value is a CacheBreakpoint; every block is then marked.
        Wire(request).GetRawText().Should().NotContain("cache_control");
    }

    // ---- Responses ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Text_and_tool_use_blocks_map_to_TextContent_and_FunctionCallContent()
    {
        var (client, _) = Client(() => Json(Message(
            """[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"location":"Amsterdam","days":[1,2]}}]""",
            "tool_use")));

        var response = await client.GetResponseAsync([new(ChatRole.User, "u")]);
        var contents = response.Messages.Should().ContainSingle().Subject.Contents;

        // Red: skip TextBlock in the response mapping; the first content is then the call.
        contents[0].Should().BeOfType<TextContent>().Which.Text.Should().Be("Let me check.");
        // Red: leave RawRepresentation unset on mapped text; this is then null.
        contents[0].RawRepresentation.Should().BeOfType<TextBlock>();
        var call = contents[1].Should().BeOfType<FunctionCallContent>().Subject;
        // Red: use the tool name as the call id; this then differs.
        call.CallId.Should().Be("toolu_1");
        // Red: use the call id as the name; this then differs.
        call.Name.Should().Be("get_weather");
        // Red: keep each argument as the string of its raw JSON; this is then a string, not a JsonElement.
        call.Arguments!["location"].Should().BeOfType<JsonElement>().Which.GetString().Should().Be("Amsterdam");
        // Red: stop after the first property when reading the input object; "days" is then missing.
        ((JsonElement)call.Arguments["days"]!).GetRawText().Should().Be("[1,2]");
        // Red: leave RawRepresentation unset on a mapped call; this is then null.
        call.RawRepresentation.Should().BeOfType<ToolUseBlock>();
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("model_context_window_exceeded", "length")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("refusal", "content_filter")]
    [InlineData("pause_turn", "pause_turn")]
    [InlineData("", "stop")]
    public async Task Stop_reasons_map_to_finish_reasons(string stopReason, string finishReason)
    {
        var (client, _) = Client(() => Json(Message("""[{"type":"text","text":"x"}]""", stopReason)));

        var response = await client.GetResponseAsync([new(ChatRole.User, "u")]);

        // Red: pass every reason on as new ChatFinishReason(raw); every row but pause_turn then fails.
        // Red: fold an unknown reason into Stop; the pause_turn row then fails.
        // Red: drop the blank-reason arm; new ChatFinishReason("") then throws in the blank row.
        response.FinishReason.Should().Be(new ChatFinishReason(finishReason));
    }

    [Fact]
    public async Task A_response_without_a_stop_reason_has_no_finish_reason()
    {
        var (client, _) = Client(() => Json(Message("""[{"type":"text","text":"x"}]""", stopReason: null)));

        var response = await client.GetResponseAsync([new(ChatRole.User, "u")]);

        // Red: map a null stop_reason to ChatFinishReason.Stop; this is then not null.
        response.FinishReason.Should().BeNull();
    }

    [Fact]
    public async Task ResponseId_ModelId_and_RawRepresentation_are_set()
    {
        var (client, _) = Client(() => Json(Read("text.response.json")));

        var response = await client.GetResponseAsync([new(ChatRole.User, "u")]);

        // Red: leave ResponseId unset; this is then null.
        response.ResponseId.Should().Be("msg_011CgUNyxuU5h61r3ritjz7R");
        // Red: leave the message's MessageId unset; this is then null.
        response.Messages.Single().MessageId.Should().Be("msg_011CgUNyxuU5h61r3ritjz7R");
        // Red: use the request's model; this is then claude-test.
        response.ModelId.Should().Be(RecordedModel);
        // Red: leave RawRepresentation unset; this is then null.
        response.RawRepresentation.Should().BeOfType<MessageResponse>();
        // Red: give the message the user role; this then differs.
        response.Messages.Single().Role.Should().Be(ChatRole.Assistant);
        // Red: leave CreatedAt unset; this is then null.
        response.CreatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_response_without_a_model_reports_the_request_model()
    {
        var (client, _) = Client(() => Json(Message("""[{"type":"text","text":"x"}]""", "end_turn").Replace("\"model\":\"claude-test\"", "\"model\":\"\"", StringComparison.Ordinal)));

        var response = await client.GetResponseAsync([new(ChatRole.User, "u")], new ChatOptions { ModelId = "claude-asked" });

        // Red: report the response's model unconditionally; this is then "".
        response.ModelId.Should().Be("claude-asked");
    }

    [Fact]
    public async Task An_unknown_block_is_skipped()
    {
        var (client, _) = Client(() => Json(Message("""[{"type":"thinking","thinking":"hmm","signature":"s"},{"type":"text","text":"ok"}]""", "end_turn")));

        var response = await client.GetResponseAsync([new(ChatRole.User, "u")]);

        // Red: map an unrecognized block to a bare AIContent, as the SDK did; there are then two contents.
        response.Messages.Single().Contents.Should().ContainSingle().Which.Should().BeOfType<TextContent>();
    }

    [Fact]
    public async Task An_api_error_throws_AnthropicApiException()
    {
        var (client, _) = Client(() => Json(
            """{"type":"error","error":{"type":"invalid_request_error","message":"bad"}}""", HttpStatusCode.BadRequest));

        var act = () => client.GetResponseAsync([new(ChatRole.User, "u")]);

        // Red: return an empty ChatResponse on a failed Result; nothing is then thrown.
        // Red: throw with the error's type replaced by "api_error"; the type then differs.
        (await act.Should().ThrowAsync<AnthropicApiException>()).Which.Error.Type.Should().Be("invalid_request_error");
    }

    [Fact]
    public async Task Usage_counts_cache_writes_and_reads_as_input()
    {
        var (client, _) = Client(() => Json(Message("""[{"type":"text","text":"x"}]""", "end_turn")));

        var usage = (await client.GetResponseAsync([new(ChatRole.User, "u")])).Usage!;

        // Red: leave cache tokens out of InputTokenCount; this is then 10.
        usage.InputTokenCount.Should().Be(60);
        // Red: report cache writes as cached input; this is then 20.
        usage.CachedInputTokenCount.Should().Be(30);
        // Red: report input as output; this is then 60.
        usage.OutputTokenCount.Should().Be(5);
        // Red: leave TotalTokenCount unset; this is then null.
        usage.TotalTokenCount.Should().Be(65);
        // Red: drop the CacheCreationInputTokens additional count; the lookup then fails.
        usage.AdditionalCounts.Should().ContainKey(AnthropicChatClient.CacheCreationInputTokensKey).WhoseValue.Should().Be(20);
    }

    [Fact]
    public async Task The_recorded_cache_read_usage_maps_from_its_numbers()
    {
        // cache-read.response.json: input_tokens 16, cache_creation_input_tokens 0, cache_read_input_tokens 14402, output_tokens 4.
        var (client, _) = Client(() => Json(Read("cache-read.response.json")));

        var usage = (await client.GetResponseAsync([new(ChatRole.User, "u")])).Usage!;

        // Red: leave cache reads out of InputTokenCount; this is then 16.
        usage.InputTokenCount.Should().Be(16 + 14402);
        // Red: report cache writes as cached input; this is then 0.
        usage.CachedInputTokenCount.Should().Be(14402);
        // Red: report input as output; this then differs.
        usage.OutputTokenCount.Should().Be(4);
        // Red: report a zero cache-write count as an additional count; the key is then present.
        (usage.AdditionalCounts?.ContainsKey(AnthropicChatClient.CacheCreationInputTokensKey) ?? false).Should().BeFalse();
    }

    // ---- Streaming ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_recorded_stream_yields_its_text_as_updates()
    {
        var (client, _) = Client(() => Sse(Read("stream-text.response.sse")));

        var updates = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "u")]));
        var response = updates.ToChatResponse();

        // Red: skip text_delta events; the text is then empty.
        response.Text.Should().Be("1 2 3 4 5");
        // Red: drop the stop reason when handling message_delta; this is then null.
        response.FinishReason.Should().Be(ChatFinishReason.Stop);
        // Red: leave ResponseId unset on updates; this is then null.
        response.ResponseId.Should().Be("msg_011CgUNz2Qov5XApN6RXx56p");
        // Red: leave ModelId unset on updates; this is then null.
        response.ModelId.Should().Be(RecordedModel);
        // Red: give updates the user role; the response then holds a user message.
        response.Messages.Should().ContainSingle().Which.Role.Should().Be(ChatRole.Assistant);
        // Red: take output_tokens from message_start; this is then 8.
        response.Usage!.OutputTokenCount.Should().Be(11);
    }

    [Fact]
    public async Task Exactly_one_UsageContent_with_the_merged_usage()
    {
        var (client, _) = Client(() => Sse(SseText(
            ("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-test","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}"""),
            ("message_stop", """{"type":"message_stop"}"""))));

        var updates = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "u")]));

        // Red: also add a UsageContent to the message_start update; there are then two.
        var usage = updates.SelectMany(u => u.Contents).OfType<UsageContent>().Should().ContainSingle().Subject.Details;
        // Red: take input and cache counts from message_delta only; this is then null.
        usage.InputTokenCount.Should().Be(60);
        // Red: report cache writes as cached input; this is then 20.
        usage.CachedInputTokenCount.Should().Be(30);
        // Red: keep message_start's output_tokens; this is then 1.
        usage.OutputTokenCount.Should().Be(5);
        // Red: leave TotalTokenCount unset on the streamed usage; this is then null.
        usage.TotalTokenCount.Should().Be(65);
        // Red: drop the CacheCreationInputTokens additional count; the key is then missing.
        usage.AdditionalCounts.Should().ContainKey(AnthropicChatClient.CacheCreationInputTokensKey).WhoseValue.Should().Be(20);
    }

    [Fact]
    public async Task A_message_delta_without_output_tokens_keeps_the_message_start_count()
    {
        var (client, _) = Client(() => Sse(SseText(
            ("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-test","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":7}}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":3}}"""),
            ("message_stop", """{"type":"message_stop"}"""))));

        var updates = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "u")]));

        // The SDK reported null here; ZeroAlloc's delta usage has an optional output_tokens, so the start count stays.
        // Red: take OutputTokens from the delta alone; this is then null.
        updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single().Details.OutputTokenCount.Should().Be(7);
    }

    [Fact]
    public async Task The_recorded_tool_stream_yields_one_FunctionCallContent_with_parsed_arguments()
    {
        var (client, _) = Client(() => Sse(Read("stream-tool-use.response.sse")));

        var updates = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "u")]));

        // Red: yield a FunctionCallContent for every input_json_delta; there are then five.
        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        // Red: use the tool name as the streamed call's id; this then differs.
        call.CallId.Should().Be("toolu_011dF6m7PWXM6CPsPfpdC61T");
        // Red: give the streamed call an empty name; this then differs.
        call.Name.Should().Be("get_weather");
        // Red: parse only the content_block_start input, {}; "location" is then missing.
        call.Arguments!["location"].Should().BeOfType<JsonElement>().Which.GetString().Should().Be("Amsterdam");
        // Red: map tool_use to Stop; the finish reason then differs.
        updates.ToChatResponse().FinishReason.Should().Be(ChatFinishReason.ToolCalls);
    }

    [Fact]
    public async Task Tool_input_split_into_many_deltas_and_an_empty_one_parses()
    {
        var (client, _) = Client(() => Sse(ToolStream("", "{\\\"a\\\":", "", "1,\\\"b\\\":[1,", "2]}")));

        var updates = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "u")]));
        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();

        // Red: keep only the last partial_json; "2]}" then fails to parse and Arguments is null.
        call.Arguments.Should().NotBeNull();
        // Red: keep each argument as the string of its raw JSON; the cast to JsonElement then throws.
        ((JsonElement)call.Arguments!["a"]!).GetRawText().Should().Be("1");
        // Red: stop after the first property when reading the input object; "b" is then missing.
        ((JsonElement)call.Arguments["b"]!).GetRawText().Should().Be("[1,2]");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tool_with_no_input_gives_empty_arguments(bool emptyDelta)
    {
        var (client, _) = Client(() => Sse(emptyDelta ? ToolStream("") : ToolStream()));

        var updates = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "u")]));
        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();

        // Red: give a call with no fragment text null arguments, as the SDK did; this is then null.
        // Red: parse the empty fragment text itself; that fails and Arguments is then null.
        call.Arguments.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task A_truncated_stream_throws_stream_incomplete()
    {
        var (client, _) = Client(() => Sse(SseText(
            ("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-test","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}"""))));
        var texts = new List<string>();

        var act = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "u")]))
            {
                texts.Add(update.Text);
            }
        };

        // Red: catch AnthropicApiException in GetStreamingResponseAsync and end the stream; nothing is then thrown.
        (await act.Should().ThrowAsync<AnthropicApiException>()).Which.Error.Type.Should().Be("stream_incomplete");
        // Red: buffer the stream before yielding; the text before the drop is then never seen.
        string.Concat(texts).Should().Be("Hel");
    }

    // ---- Client ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Metadata_names_anthropic_and_the_default_model()
    {
        var (client, _) = Client(() => Json(Read("text.response.json")));

        var metadata = client.GetService<ChatClientMetadata>();

        // Red: name the provider "claude"; this then differs.
        metadata!.ProviderName.Should().Be("anthropic");
        // Red: leave ProviderUri null; this then differs.
        metadata.ProviderUri.Should().Be(new Uri("https://api.anthropic.com/"));
        // Red: leave DefaultModelId null; this then differs.
        metadata.DefaultModelId.Should().Be(DefaultModel);
    }

    [Fact]
    public void GetService_returns_the_client_and_itself_and_nothing_for_a_key()
    {
        var (client, _) = Client(() => Json(Read("text.response.json")));

        // Red: return null for AnthropicClient; this is then null.
        client.GetService<AnthropicClient>().Should().NotBeNull();
        // Red: return null for the adapter's own type; this is then null.
        client.GetService<IChatClient>().Should().BeSameAs(client);
        // Red: ignore serviceKey; the metadata is then returned.
        client.GetService(typeof(ChatClientMetadata), "key").Should().BeNull();
    }

    [Fact]
    public void A_non_positive_default_max_tokens_is_refused()
    {
        var api = AnthropicClient.Create(new HttpClient(new StubHandler(() => Json("{}"))), new AnthropicClientOptions { ApiKey = "sk-test" });

        var act = () => new AnthropicChatClient(api, DefaultModel, 0);

        // Red: drop the max-tokens guard from the constructor; nothing is then thrown.
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static AIFunctionDeclaration Declaration(string name, string? description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return AIFunctionFactory.CreateDeclaration(name, description, document.RootElement.Clone());
    }

    /// <summary>The input schema the request sends for <paramref name="function"/>, as a node tree.</summary>
    private static JsonNode ToolSchema(AIFunctionDeclaration function) =>
        JsonNode.Parse(Map([new(ChatRole.User, "u")], new ChatOptions { Tools = [function] }).Tools![0].InputSchema.GetRawText())!;

    /// <summary><paramref name="body"/> with every tool's <c>input_schema</c> removed; the first one is returned apart.</summary>
    private static string WithoutInputSchemas(string body, out string? firstSchema)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        firstSchema = null;
        foreach (var tool in node["tools"]?.AsArray() ?? [])
        {
            var toolObject = tool!.AsObject();
            firstSchema ??= toolObject["input_schema"]?.ToJsonString();
            toolObject.Remove("input_schema");
        }

        return node.ToJsonString();
    }

    private static MessageRequest Map(IEnumerable<ChatMessage> messages, ChatOptions? options = null) =>
        AnthropicRequestMapper.Map(messages, options, DefaultModel, DefaultMaxTokens);

    /// <summary>The request as Anthropic receives it, written by the library's own serializer.</summary>
    private static JsonElement Wire(MessageRequest request)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, AnthropicJson.MessageRequest));
        return document.RootElement.Clone();
    }

    private static string MessagesJson(MessageRequest request) => Wire(request).GetProperty("messages").GetRawText();

    private static List<string> SystemTexts(MessageRequest request) =>
        [.. request.System!.Value.Blocks!.Select(static b => b.Text)];

    private static List<string?> ToolResultContents(MessageRequest request) =>
        [.. request.Messages.SelectMany(static m => m.Content).OfType<ToolResultBlockParam>().Select(static b => b.Content)];

    private static string? CacheControlOf(JsonElement block) =>
        block.TryGetProperty("cache_control", out var control) ? control.GetRawText() : null;

    private static string Read(string file) => File.ReadAllText(Path.Combine(Folder, file));

    /// <summary>A message response with the given content array, stop reason and usage 10 / 20 / 30 / 5.</summary>
    private static string Message(string content, string? stopReason) =>
        $$$"""{"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":{{{content}}},"stop_reason":{{{(stopReason is null ? "null" : "\"" + stopReason + "\"")}}},"stop_sequence":null,"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":5}}""";

    /// <summary>A tool-use stream whose input arrives as <paramref name="partials"/>, already JSON-escaped.</summary>
    private static string ToolStream(params string[] partials) => SseText(
    [
        ("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-test","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":1}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"f","input":{}}}"""),
        .. partials.Select(static p => ("content_block_delta", $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{{{p}}}"}}""")),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}"""),
        ("message_stop", """{"type":"message_stop"}"""),
    ]);

    private static string SseText(params (string Event, string Data)[] events)
    {
        var text = new StringBuilder();
        foreach (var (name, data) in events)
        {
            text.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data).Append("\n\n");
        }

        return text.ToString();
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static (AnthropicChatClient Client, StubHandler Handler) Client(Func<HttpResponseMessage> respond, string model = DefaultModel)
    {
        var handler = new StubHandler(respond);
        var api = AnthropicClient.Create(new HttpClient(handler), new AnthropicClientOptions { ApiKey = "sk-test", MaxRetries = 0 });
        return (new AnthropicChatClient(api, model, DefaultMaxTokens), handler);
    }

    private static async Task<List<ChatResponseUpdate>> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var update in updates)
        {
            list.Add(update);
        }

        return list;
    }

    /// <summary>Records each request body and answers with <c>respond</c>.</summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return respond();
        }
    }
}
