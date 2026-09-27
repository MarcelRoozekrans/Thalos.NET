using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Caching;
using Thalos.Testing;
using Thalos.Tests.Unit.Runtime;

namespace Thalos.Tests.Unit.Caching;

/// <summary>
/// <see cref="PromptCachingChatClient"/> over a recording <see cref="ScriptedChatClient"/>, through
/// <see cref="ChatClientBuilder"/> with Microsoft.Extensions.AI's function invocation outside it, and through the real
/// runtime and agent factory with <see cref="PromptCachingDecorator"/> registered.
/// </summary>
public sealed class PromptCachingChatClientTests
{
    private static (IChatClient Client, ScriptedChatClient Inner) Build(int replies = 1)
    {
        var inner = new ScriptedChatClient();
        for (var i = 0; i < replies; i++)
        {
            inner.ThenText("ok");
        }

        return (new PromptCachingChatClient(inner), inner);
    }

    private static AIFunction Fn(string name) => AIFunctionFactory.Create(() => name, name);

    private static AIFunction FnWithProperty(string name) => AIFunctionFactory.Create(() => 1, new AIFunctionFactoryOptions
    {
        Name = name,
        AdditionalProperties = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tool-key"] = 1 },
    });

    private static bool IsHinted(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue(PromptCacheHints.Breakpoint, out var value) == true && value is true;

    private static bool IsHinted(AITool tool) =>
        tool.AdditionalProperties.TryGetValue(PromptCacheHints.Breakpoint, out var value) && value is true;

    private static bool IsInstructionsHinted(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(PromptCacheHints.InstructionsBreakpoint, out var value) == true && value is true;

    // ---------- placement ----------

    [Fact]
    public async Task Hints_land_on_the_last_tool_the_instructions_and_the_latest_message()
    {
        var (client, inner) = Build();
        var options = new ChatOptions { Instructions = "sys", Tools = [Fn("a"), Fn("b")] };
        var messages = new List<ChatMessage> { new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2") };

        await client.GetResponseAsync(messages, options, CancellationToken.None);

        var seen = inner.Requests[^1];
        IsHinted(seen.Options!.Tools![1]).Should().BeTrue();
        IsHinted(seen.Options.Tools[0]).Should().BeFalse();
        IsInstructionsHinted(seen.Options).Should().BeTrue();
        seen.Messages.Select(m => m.Text).Should().Equal("q1", "a1", "q2");
        IsHinted(seen.Messages[^1]).Should().BeTrue();
        seen.Messages.Count(IsHinted).Should().Be(1);
    }

    [Fact]
    public async Task The_streaming_path_places_the_same_hints_without_touching_the_callers_message()
    {
        var (client, inner) = Build();
        var options = new ChatOptions { Instructions = "sys", Tools = [Fn("a"), Fn("b")] };
        var messages = new List<ChatMessage> { new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2") };

        await client.GetStreamingResponseAsync(messages, options, CancellationToken.None).ToListAsync();

        var seen = inner.Requests[^1];
        IsHinted(seen.Options!.Tools![1]).Should().BeTrue();
        IsHinted(seen.Options.Tools[0]).Should().BeFalse();
        IsInstructionsHinted(seen.Options).Should().BeTrue();
        seen.Messages.Select(m => m.Text).Should().Equal("q1", "a1", "q2");
        IsHinted(seen.Messages[^1]).Should().BeTrue();
        seen.Messages.Count(IsHinted).Should().Be(1);
        messages[^1].AdditionalProperties.Should().BeNull();
    }

    [Fact]
    public async Task A_second_round_trip_has_exactly_one_message_hint()
    {
        var (client, inner) = Build(replies: 2);
        var messages = new List<ChatMessage> { new(ChatRole.User, "q1") };

        var first = await client.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);
        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(ChatRole.User, "q2"));
        await client.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        var second = inner.Requests[^1].Messages;
        second.Count(IsHinted).Should().Be(1);
        IsHinted(second[^1]).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_instructions_add_no_instructions_hint()
    {
        var (client, inner) = Build(replies: 2);

        await client.GetResponseAsync([new(ChatRole.User, "q")], new ChatOptions { Instructions = "" }, CancellationToken.None);
        await client.GetResponseAsync([new(ChatRole.User, "q")], new ChatOptions { Instructions = null }, CancellationToken.None);

        IsInstructionsHinted(inner.Requests[0].Options).Should().BeFalse();
        IsInstructionsHinted(inner.Requests[1].Options).Should().BeFalse();
    }

    [Fact]
    public async Task A_last_tool_that_is_not_a_function_is_left_unhinted_and_no_other_tool_is_hinted_instead()
    {
        var (client, inner) = Build();
        var hosted = new HostedWebSearchTool();
        var options = new ChatOptions { Tools = [Fn("a"), hosted] };

        await client.GetResponseAsync([new(ChatRole.User, "q")], options, CancellationToken.None);

        var tools = inner.Requests[^1].Options!.Tools!;
        tools[1].Should().BeSameAs(hosted);
        tools.Should().NotContain(t => IsHinted(t));
    }

    // ---------- edge cases ----------

    [Fact]
    public async Task Empty_messages_null_options_and_no_tools_do_not_throw()
    {
        var (client, inner) = Build(replies: 4);

        await FluentActions.Awaiting(() => client.GetResponseAsync([], null, CancellationToken.None)).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => client.GetResponseAsync([new(ChatRole.User, "q")], null, CancellationToken.None)).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => client.GetResponseAsync([new(ChatRole.User, "q")], new ChatOptions { Tools = [] }, CancellationToken.None)).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => client.GetStreamingResponseAsync([], null, CancellationToken.None).ToListAsync().AsTask()).Should().NotThrowAsync();

        inner.Requests[0].Messages.Should().BeEmpty();
        inner.Requests[0].Options.Should().BeNull();
        inner.Requests[1].Options.Should().BeNull();
        IsHinted(inner.Requests[1].Messages[0]).Should().BeTrue();
        inner.Requests[2].Options!.Tools.Should().NotBeNull().And.BeEmpty();
        inner.Requests[3].Messages.Should().BeEmpty();
    }

    // ---------- the caller's objects ----------

    [Fact]
    public async Task The_callers_messages_and_options_are_never_mutated()
    {
        var (client, _) = Build();
        var tool = Fn("a");
        var options = new ChatOptions { Instructions = "sys", Tools = [tool] };
        var callerTools = options.Tools;
        var last = new ChatMessage(ChatRole.User, "q");
        var callerContents = last.Contents;
        var messages = new List<ChatMessage> { last };

        await client.GetResponseAsync(messages, options, CancellationToken.None);

        messages.Should().ContainSingle().Which.Should().BeSameAs(last);
        last.AdditionalProperties.Should().BeNull("a hint left in history would accumulate breakpoints lap after lap");
        last.Contents.Should().BeSameAs(callerContents);
        last.Contents.Should().ContainSingle();
        options.AdditionalProperties.Should().BeNull();
        IsHinted(options.Tools![0]).Should().BeFalse();
        options.Tools.Should().BeSameAs(callerTools);
        options.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
    }

    [Fact]
    public async Task The_callers_existing_additional_properties_dictionaries_are_not_written_to()
    {
        var (client, _) = Build();
        var tool = FnWithProperty("a");
        var options = new ChatOptions { Instructions = "sys", Tools = [tool], AdditionalProperties = new() { ["options-key"] = 2 } };
        var last = new ChatMessage(ChatRole.User, "q") { AdditionalProperties = new() { ["message-key"] = 3 } };

        await client.GetResponseAsync([last], options, CancellationToken.None);

        options.AdditionalProperties.Keys.Should().Equal("options-key");
        last.AdditionalProperties.Keys.Should().Equal("message-key");
        tool.AdditionalProperties.Keys.Should().Equal("tool-key");
    }

    [Fact]
    public async Task The_existing_additional_properties_are_carried_onto_the_hinted_copies()
    {
        var (client, inner) = Build();
        var options = new ChatOptions { Instructions = "sys", Tools = [FnWithProperty("a")], AdditionalProperties = new() { ["options-key"] = 2 } };
        var last = new ChatMessage(ChatRole.User, "q") { AdditionalProperties = new() { ["message-key"] = 3 } };

        await client.GetResponseAsync([last], options, CancellationToken.None);

        var seen = inner.Requests[^1];
        seen.Options!.AdditionalProperties.Should().Contain("options-key", 2);
        seen.Options.Tools![0].AdditionalProperties.Should().Contain("tool-key", 1);
        seen.Messages[0].AdditionalProperties.Should().Contain("message-key", 3);
    }

    [Fact]
    public async Task The_latest_message_copy_keeps_every_member_of_the_callers_message()
    {
        var (client, inner) = Build();
        var raw = new object();
        var contents = new List<AIContent> { new TextContent("result"), new FunctionResultContent("call-1", "42") };
        var last = new ChatMessage(ChatRole.Tool, contents)
        {
            AuthorName = "author",
            MessageId = "message-1",
            CreatedAt = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero),
            RawRepresentation = raw,
            AdditionalProperties = new() { ["message-key"] = 3 },
        };

        await client.GetResponseAsync([last], null, CancellationToken.None);

        var copy = inner.Requests[^1].Messages[^1];
        copy.Should().NotBeSameAs(last);
        copy.Role.Should().Be(ChatRole.Tool);
        copy.AuthorName.Should().Be("author");
        copy.MessageId.Should().Be("message-1");
        copy.CreatedAt.Should().Be(last.CreatedAt);
        copy.RawRepresentation.Should().BeSameAs(raw);
        copy.Contents.Should().NotBeSameAs(contents, "a component further in that edits the list must not edit the caller's message");
        copy.Contents.Should().HaveCount(2);
        copy.Contents[0].Should().BeSameAs(contents[0]);
        copy.Contents[1].Should().BeSameAs(contents[1]);
        copy.AdditionalProperties.Should().NotBeNull().And.ContainKeys("message-key", PromptCacheHints.Breakpoint).And.HaveCount(2);

        // A member added to ChatMessage by a later Microsoft.Extensions.AI release must survive too.
        var others = typeof(ChatMessage).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.Name is not nameof(ChatMessage.Contents) and not nameof(ChatMessage.AdditionalProperties))
            .ToList();
        others.Should().NotBeEmpty();
        foreach (var property in others)
        {
            property.GetValue(copy).Should().Be(property.GetValue(last), $"{property.Name} must be copied");
        }
    }

    // ---------- the hinted tool ----------

    [Fact]
    public async Task The_hinted_tool_describes_and_invokes_exactly_like_the_inner_function()
    {
        var (client, inner) = Build();
        var calls = new List<string>();
        var function = AIFunctionFactory.Create(
            (string text) =>
            {
                calls.Add(text);
                return "echo:" + text;
            },
            new AIFunctionFactoryOptions { Name = "echo", Description = "Echoes the text." });

        await client.GetResponseAsync([new(ChatRole.User, "q")], new ChatOptions { Tools = [function] }, CancellationToken.None);

        var hinted = inner.Requests[^1].Options!.Tools![0].Should().BeAssignableTo<AIFunction>().Subject;
        hinted.Should().NotBeSameAs(function);
        hinted.Name.Should().Be("echo");
        hinted.Description.Should().Be("Echoes the text.");
        hinted.JsonSchema.GetRawText().Should().Be(function.JsonSchema.GetRawText());
        (hinted.ReturnJsonSchema?.GetRawText()).Should().Be(function.ReturnJsonSchema!.Value.GetRawText());
        hinted.JsonSerializerOptions.Should().BeSameAs(function.JsonSerializerOptions);
        hinted.UnderlyingMethod.Should().BeSameAs(function.UnderlyingMethod);

        var result = await hinted.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "hi" }, CancellationToken.None);

        calls.Should().Equal("hi");
        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("echo:hi");
    }

    [Fact]
    public async Task Function_invocation_outside_the_client_reaches_the_real_tool_and_every_round_trip_hints_its_latest_message()
    {
        var scripted = new ScriptedChatClient().ThenToolCall("echo", new { text = "x" }).ThenText("done");
        var calls = new List<string>();
        var echo = AIFunctionFactory.Create(
            (string text) =>
            {
                calls.Add(text);
                return "echo:" + text;
            },
            "echo");
        using var client = new ChatClientBuilder(scripted).UseFunctionInvocation().UsePromptCaching().Build();

        await client.GetResponseAsync([new(ChatRole.User, "go")], new ChatOptions { Tools = [echo] }, CancellationToken.None);

        calls.Should().Equal("x");
        scripted.Requests.Should().HaveCount(2);
        IsHinted(scripted.Requests[0].Messages[^1]).Should().BeTrue();
        scripted.Requests[1].Messages[^1].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionResultContent>();
        IsHinted(scripted.Requests[1].Messages[^1]).Should().BeTrue();
        scripted.Requests.Should().AllSatisfy(r => r.Messages.Count(IsHinted).Should().Be(1));
        scripted.Requests.Should().AllSatisfy(r => IsHinted(r.Options!.Tools![0]).Should().BeTrue());
    }

    // ---------- through the agent factory ----------

    [Fact]
    public async Task Through_the_runtime_each_round_trip_of_a_tool_calling_turn_is_hinted()
    {
        var calls = new List<string>();
        var f = new RuntimeFixture().WithTool(AIFunctionFactory.Create(
            (string text) =>
            {
                calls.Add(text);
                return "echo:" + text;
            },
            "echo"));
        f.Decorators.Add(new PromptCachingDecorator());
        f.Build();
        f.Client.ThenToolCall("t__echo", new { text = "x" }).ThenText("done");
        var session = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), CancellationToken.None)).Value;

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", RuntimeFixture.User()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        calls.Should().Equal("x");
        f.Client.Requests.Should().HaveCount(2);
        f.Client.Requests[0].Messages[^1].Text.Should().Be("go");
        f.Client.Requests[1].Messages[^1].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionResultContent>();
        f.Client.Requests.Should().AllSatisfy(r => r.Messages.Count(IsHinted).Should().Be(1));
        f.Client.Requests.Should().AllSatisfy(r => IsHinted(r.Messages[^1]).Should().BeTrue());
        f.Client.Requests.Should().AllSatisfy(r => IsInstructionsHinted(r.Options).Should().BeTrue());
        f.Client.Requests.Should().AllSatisfy(r => IsHinted(r.Options!.Tools![^1]).Should().BeTrue());
    }

    [Fact]
    public async Task Through_the_runtime_no_hint_reaches_the_stored_history()
    {
        var f = new RuntimeFixture();
        f.Decorators.Add(new PromptCachingDecorator());
        f.Build();
        f.Client.ThenText("first answer").ThenText("second answer");
        var session = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), CancellationToken.None)).Value;

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "first question", RuntimeFixture.User()), CancellationToken.None);
        await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "second question", RuntimeFixture.User()), CancellationToken.None);

        var stored = (await f.Store.LoadMessagesAsync(session, CancellationToken.None)).Value;
        stored.Should().HaveCount(4, "both questions and both answers are stored");
        stored.Should().NotContain(m => IsHinted(m));
        f.Client.Requests[^1].Messages.Count(IsHinted).Should().Be(1, "only the latest message of the second turn carries a hint");
    }

    // ---------- registration ----------

    [Fact]
    public void UsePromptCaching_registers_one_outermost_decorator_however_often_it_is_called()
    {
        var services = new ServiceCollection();
        services.AddThalos(b => b.UsePromptCaching().UsePromptCaching());
        using var provider = services.BuildServiceProvider();

        var decorator = provider.GetServices<IChatClientDecorator>().Should().ContainSingle().Subject;
        decorator.Order.Should().Be(int.MaxValue);
        decorator.Decorate(new ScriptedChatClient(), new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "sys" }, provider)
            .Should().BeOfType<PromptCachingChatClient>();
    }

    [Fact]
    public void The_chat_client_builder_extension_adds_the_client()
    {
        using var client = new ChatClientBuilder(new ScriptedChatClient()).UsePromptCaching().Build();

        client.GetService<PromptCachingChatClient>().Should().NotBeNull();
    }
}
