using Thalos.Tests.Channels.Fakes;

namespace Thalos.Tests.Channels;

/// <summary>
/// Covers both ways <c>CreateAndBindAsync</c> can fail to start a session — the runtime refusing to create one, and
/// the conversation map refusing to persist one that was created — because both are the same rule: the operator is
/// always told something, never left in silence just because the failure happened before a turn could run.
/// </summary>
public sealed class ChannelPumpSessionStartFailureTests
{
    [Fact]
    public async Task Operator_is_told_when_the_conversation_map_fails_to_bind()
    {
        using var h = new PumpHarness();
        h.FailNextBind(AgentError.StoreError("bind failed"));

        await h.SendAndSettle("hello");

        h.Channel.Delivered.Should().NotBeEmpty(
            "the operator must always be told something, even when binding fails");
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull(
            "a bind failure must not leave the pump believing a binding was persisted");
    }

    /// <summary>
    /// Mirrors the bind-failure test above, but for the OTHER way <c>CreateAndBindAsync</c> can return null: the
    /// runtime itself refuses to create a session. Before this fix, that branch only logged (event 603) and
    /// returned null — both of its callers either discard that return value (<c>StartNewAsync</c>, the /new
    /// command) or just return on a null binding without a notice of their own (<c>ResolveAsync</c>'s caller,
    /// <c>RunTrackedTurnAsync</c>), so the operator got nothing at all: no log they can see, no notice, silence.
    /// </summary>
    [Fact]
    public async Task Operator_is_told_when_the_runtime_fails_to_create_a_session()
    {
        using var h = new PumpHarness();
        h.FailNextSessionCreate(AgentError.ProviderError("create failed"));

        await h.SendAndSettle("hello");

        h.Channel.Delivered.Should().NotBeEmpty(
            "the operator must always be told something, even when the runtime cannot create a session");
        (await h.Map.GetAsync("fake", h.Channel.Conversation, default)).Value.Should().BeNull(
            "a session-creation failure must not leave anything bound — there is no session to bind to");
    }
}
