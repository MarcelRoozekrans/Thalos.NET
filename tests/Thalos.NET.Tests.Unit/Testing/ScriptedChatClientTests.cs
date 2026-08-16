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
