using Microsoft.Extensions.AI;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Runtime;

/// <summary>
/// A turn may ask for a model and a narrower tool list, and the engine refuses anything outside the agent's own
/// configuration before the session is claimed and before any chat request is sent. Tool absence is a security
/// boundary for the host, so the check lives here and not only in the caller.
/// </summary>
public sealed class TurnOverrideValidationTests
{
    private static readonly ISecurityContext Caller = RuntimeFixture.User();

    private static AIFunction Echo() => AIFunctionFactory.Create((string text) => "echo:" + text, "echo");

    private static async Task<(RuntimeFixture F, SessionId Session)> ReadyAsync(Action<RuntimeFixture>? configure = null)
    {
        var f = new RuntimeFixture().WithTool(Echo());
        configure?.Invoke(f);
        f.Build();
        f.Client.ThenText("done");
        var session = await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, CancellationToken.None);
        return (f, session.Value);
    }

    /// <summary>Red: the membership test against AllowedModels is removed, since the model is then refused.</summary>
    [Fact]
    public async Task A_model_override_in_the_allowed_list_is_accepted()
    {
        var (f, session) = await ReadyAsync(x => x.WithAllowedModels("big-model"));

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "big-model" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        f.Client.Requests.Should().HaveCount(1);
    }

    /// <summary>Red: the default-model branch is removed: the agent here lists no other model, so only the default can admit it.</summary>
    [Fact]
    public async Task A_model_override_equal_to_the_agents_own_model_is_accepted_without_an_allowed_list()
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "m1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: the fallback to the provider default is dropped (the agent has no model of its own here).</summary>
    [Fact]
    public async Task A_model_override_equal_to_the_provider_default_is_accepted_when_the_agent_names_no_model()
    {
        var (f, session) = await ReadyAsync(x => x.WithModel(null));

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "dm" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Red: validation is removed (the turn runs and a chat request is sent), when the message text changes, and
    /// when the code changes from Validation. The request count of zero goes red if the check moves after the model call.
    /// </summary>
    [Fact]
    public async Task A_model_override_outside_the_allowed_list_is_refused()
    {
        var (f, session) = await ReadyAsync(x => x.WithAllowedModels("big-model"));

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "rogue" }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        result.Error.Message.Should().Be("Model 'rogue' is not in agent 'a' allowed models.");
        f.Client.Requests.Should().BeEmpty();
    }

    /// <summary>Red: moving the validation below the session claim leaves the session Running, so the next turn fails as busy (verified).</summary>
    [Fact]
    public async Task A_refused_override_leaves_the_session_usable()
    {
        var (f, session) = await ReadyAsync();

        var refused = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "rogue" }, CancellationToken.None);
        var next = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "again", Caller), CancellationToken.None);

        refused.IsFailure.Should().BeTrue();
        next.IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: the match is dropped (the filter is then refused) or when the glob is not run through Glob.IsMatch.</summary>
    [Fact]
    public async Task A_tool_filter_entry_matching_one_of_the_agents_tools_is_accepted()
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["t__*"] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Red: the tool check is removed. The offending entry is the second one, so a check of only the first entry
    /// goes red too.
    /// </summary>
    [Fact]
    public async Task A_tool_filter_entry_matching_none_of_the_agents_tools_is_refused()
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["t__echo", "other__*"] }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        result.Error.Message.Should().Be("Tool filter entry 'other__*' matches none of agent 'a' tools.");
        f.Client.Requests.Should().BeEmpty();
    }

    /// <summary>Red: validation matches against the filter-narrowed set or the host's whole catalog instead of the agent's own tools.</summary>
    [Fact]
    public async Task A_tool_the_host_has_but_the_agent_is_not_configured_for_is_refused()
    {
        var f = new RuntimeFixture("t__other").WithTool(Echo()).Build();
        f.Client.ThenText("done");
        var session = (await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, CancellationToken.None)).Value;

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["t__echo"] }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Be("Tool filter entry 't__echo' matches none of agent 'a' tools.");
    }

    /// <summary>Red: null starts to be treated as an empty filter or an unknown model, which would refuse an ordinary turn.</summary>
    [Fact]
    public async Task No_override_behaves_exactly_as_before()
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        f.Client.Requests.Should().HaveCount(1);
    }

    /// <summary>Red: dropping the null and blank check lets Glob.IsMatch throw ArgumentNullException out of the turn (verified).</summary>
    [Fact]
    public async Task A_null_tool_filter_entry_is_refused_not_thrown()
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["t__echo", null!] }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        result.Error.Message.Should().Be("Tool filter entry must not be blank.");
        f.Client.Requests.Should().BeEmpty();
    }

    /// <summary>Red: removing the whitespace check lets "  " reach the match and report a different message.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_tool_filter_entry_is_refused(string entry)
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = [entry] }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        result.Error.Message.Should().Be("Tool filter entry must not be blank.");
    }

    /// <summary>Red: removing the empty-list check lets [] pass as a no-op filter.</summary>
    [Fact]
    public async Task An_empty_tool_filter_is_refused()
    {
        var (f, session) = await ReadyAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = [] }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        result.Error.Message.Should().Be("Tool filter must not be empty; pass null for no filter.");
        f.Client.Requests.Should().BeEmpty();
    }
}
