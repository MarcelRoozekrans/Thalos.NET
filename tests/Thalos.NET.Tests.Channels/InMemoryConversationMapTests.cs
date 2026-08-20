using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class InMemoryConversationMapTests
{
    // AgentId is a Guid-backed [TypedId] (see Thalos.NET.Abstractions/Ids.cs), unlike the hand-written string-backed
    // ConversationId, so bindings are built from AgentId.New() rather than a literal name.
    private static ConversationBinding Binding(string conversation = "42", AgentId? agent = null) =>
        new("telegram", new ConversationId(conversation), SessionId.New(), agent ?? AgentId.New(), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Unknown_conversation_returns_null_not_an_error()
    {
        var map = new InMemoryConversationMap();
        var result = await map.GetAsync("telegram", new ConversationId("nope"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Bind_then_get_round_trips()
    {
        var map = new InMemoryConversationMap();
        var binding = Binding();

        (await map.BindAsync(binding, default)).IsSuccess.Should().BeTrue();
        var found = await map.GetAsync("telegram", new ConversationId("42"), default);

        found.Value.Should().NotBeNull();
        found.Value!.SessionId.Should().Be(binding.SessionId);
        found.Value.AgentId.Should().Be(binding.AgentId);
    }

    [Fact]
    public async Task Bind_replaces_an_existing_binding_for_the_same_conversation()
    {
        var map = new InMemoryConversationMap();
        await map.BindAsync(Binding(), default);
        var second = Binding();
        await map.BindAsync(second, default);

        var found = await map.GetAsync("telegram", new ConversationId("42"), default);
        found.Value!.SessionId.Should().Be(second.SessionId);
    }

    [Fact]
    public async Task Bindings_are_scoped_by_channel()
    {
        var map = new InMemoryConversationMap();
        await map.BindAsync(Binding(), default);

        var otherChannel = await map.GetAsync("console", new ConversationId("42"), default);
        otherChannel.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetBySession_finds_the_conversation_an_adapter_must_answer()
    {
        var map = new InMemoryConversationMap();
        var binding = Binding();
        await map.BindAsync(binding, default);

        var found = await map.GetBySessionAsync(binding.SessionId, default);
        found.Value!.ConversationId.Value.Should().Be("42");
    }

    [Fact]
    public async Task GetBySession_returns_null_for_a_session_no_conversation_is_serving()
    {
        var map = new InMemoryConversationMap();
        (await map.GetBySessionAsync(SessionId.New(), default)).Value.Should().BeNull();
    }

    [Fact]
    public async Task Unbind_removes_it_and_is_idempotent()
    {
        var map = new InMemoryConversationMap();
        await map.BindAsync(Binding(), default);

        (await map.UnbindAsync("telegram", new ConversationId("42"), default)).IsSuccess.Should().BeTrue();
        (await map.GetAsync("telegram", new ConversationId("42"), default)).Value.Should().BeNull();
        (await map.UnbindAsync("telegram", new ConversationId("42"), default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_binding_stored_under_an_empty_id_is_retrievable_via_default_ConversationId()
    {
        // Sharp edge: ConversationId is a record struct whose Value normalizes null to "" on read, but whose
        // compiler-generated equality compares the raw backing field, so default(ConversationId) != new ConversationId("")
        // even though both expose Value == "". The map must key on conversationId.Value (the normalized string), not on
        // the struct itself, or these two "identical" ids could land in different dictionary slots.
        var map = new InMemoryConversationMap();
        var binding = Binding(conversation: "");
        await map.BindAsync(binding, default);

        default(ConversationId).Should().NotBe(new ConversationId(""));

        var found = await map.GetAsync("telegram", default, default);
        found.Value.Should().NotBeNull();
        found.Value!.SessionId.Should().Be(binding.SessionId);
    }
}
