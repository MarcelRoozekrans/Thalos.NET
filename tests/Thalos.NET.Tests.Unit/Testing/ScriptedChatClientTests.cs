using System.Text;
using Microsoft.Extensions.AI;
using Thalos.Testing;

namespace Thalos.Tests.Unit.Testing;

public sealed class ScriptedChatClientTests
{
    [Fact]
    public async Task Replays_text_then_tool_call_then_text_and_records_requests()
    {
        var client = new ScriptedChatClient()
            .ThenText("first", input: 10, output: 2)
            .ThenToolCall("echo", new { text = "x" }, callId: "c1")
            .ThenText("second");

        var r1 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        r1.Text.Should().Be("first");
        r1.Usage!.InputTokenCount.Should().Be(10);
        r1.Usage.OutputTokenCount.Should().Be(2);

        var r2 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]);
        var call = r2.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
        call.Name.Should().Be("echo");
        call.CallId.Should().Be("c1");
        call.Arguments!["text"].Should().Be("x");

        var r3 = await client.GetResponseAsync([]);
        r3.Text.Should().Be("second");

        client.Requests.Should().HaveCount(3);
        client.Requests[1].Messages.Single().Text.Should().Be("again");
    }

    [Fact]
    public async Task Streaming_yields_the_same_content_as_updates()
    {
        var client = new ScriptedChatClient().ThenText("hello world");
        var text = new StringBuilder();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]))
        {
            text.Append(u.Text);
        }
        text.ToString().Should().Be("hello world");
    }

    [Fact]
    public async Task Tool_call_with_preceding_text_puts_text_before_the_call_and_streams_it_first()
    {
        var client = new ScriptedChatClient().ThenToolCall("echo", new { text = "x" }, callId: "c1", precedingText: "Let me check.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]))
        {
            updates.Add(u);
        }

        var textDeltas = updates.TakeWhile(u => u.Contents.All(c => c is TextContent)).ToList();
        string.Concat(textDeltas.Select(u => u.Text)).Should().Be("Let me check.");
        textDeltas.Should().HaveCount(3);
        var callUpdate = updates[textDeltas.Count];
        callUpdate.Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionCallContent>().Which.CallId.Should().Be("c1");
        updates[^1].Contents.Should().ContainSingle().Which.Should().BeOfType<UsageContent>();
        updates.Should().HaveCount(textDeltas.Count + 2);
    }

    [Fact]
    public async Task Tool_call_with_preceding_text_is_one_assistant_message_when_buffered()
    {
        var client = new ScriptedChatClient().ThenToolCall("echo", new { text = "x" }, precedingText: "Let me check.");
        var r = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);
        var contents = r.Messages.Single().Contents;
        contents.Should().HaveCount(2);
        contents[0].Should().BeOfType<TextContent>().Which.Text.Should().Be("Let me check.");
        contents[1].Should().BeOfType<FunctionCallContent>().Which.Name.Should().Be("echo");
        r.FinishReason.Should().Be(ChatFinishReason.ToolCalls);
    }

    [Fact]
    public async Task Exhausted_script_throws()
    {
        var client = new ScriptedChatClient();
        var act = () => client.GetResponseAsync([]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*script*exhausted*");
    }

    [Fact]
    public async Task ThenThrow_throws_the_configured_exception()
    {
        var client = new ScriptedChatClient().ThenThrow(new HttpRequestException("boom"));
        var act = () => client.GetResponseAsync([]);
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
