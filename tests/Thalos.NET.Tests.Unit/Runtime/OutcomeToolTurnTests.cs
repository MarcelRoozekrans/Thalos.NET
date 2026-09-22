using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Thalos.Tests.Unit.Runtime;

/// <summary>
/// End-to-end over the real runtime, the real agent factory, the real tool catalog and the real MAF pipeline — only
/// the model is scripted. These are the tests that would have caught <see cref="SubagentRunRequest.RequiredOutcome"/>
/// being a field nothing read: they assert against the <see cref="ChatOptions"/> the provider was actually handed,
/// not against an intermediate the production code could satisfy without the model ever seeing the tool.
/// </summary>
public sealed class OutcomeToolTurnTests
{
    private static readonly OutcomeToolSchema Approval = new("workflow__report_outcome", ["approved", "rejected"]);

    private static AIFunction Echo() => AIFunctionFactory.Create((string text) => "echo:" + text, "echo");

    private static IReadOnlyList<string> ToolNamesSeenByTheModel(Thalos.Testing.ScriptedChatClient client) =>
        [.. (client.Requests[0].Options?.Tools ?? []).Select(t => t.Name)];

    /// <summary>
    /// The core claim of this task. Red if <c>ThalosAgentRuntime.BuildRunOptions</c> stops building options, if the
    /// options stop being passed to <c>RunStreamingAsync</c>, or if the tool is built without its enum.
    /// </summary>
    [Fact]
    public async Task Turn_with_a_required_outcome_offers_the_enum_constrained_tool_to_the_model()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = Approval }, default);

        r.IsSuccess.Should().BeTrue();
        var offered = (f.Client.Requests[0].Options?.Tools ?? []).Single(t => string.Equals(t.Name, "workflow__report_outcome", StringComparison.Ordinal));
        var schema = offered.Should().BeAssignableTo<AIFunctionDeclaration>().Which.JsonSchema;
        schema.GetProperty("properties").GetProperty(OutcomeToolSchema.ArgumentName)
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("approved", "rejected");
    }

    /// <summary>
    /// Requirement: a request that names no outcome must be indistinguishable from one made before this feature
    /// existed. Red if the outcome tool is ever added unconditionally, or if a non-null run-options object starts
    /// being built for every turn.
    /// </summary>
    [Fact]
    public async Task Turn_without_a_required_outcome_sees_exactly_the_agents_own_tools()
    {
        var f = new RuntimeFixture().WithTool(Echo()).Build();
        f.Client.ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()), default);

        r.IsSuccess.Should().BeTrue();
        ToolNamesSeenByTheModel(f.Client).Should().Equal("t__echo");
    }

    /// <summary>
    /// The same assertion for an agent with no tools at all: adding the outcome tool unconditionally would turn a
    /// null tool list into a one-element one, which is a different request to the provider. Red if that happens.
    /// </summary>
    [Fact]
    public async Task Turn_without_a_required_outcome_on_a_toolless_agent_still_offers_nothing()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()), default);

        (f.Client.Requests[0].Options?.Tools).Should().BeNullOrEmpty();
    }

    /// <summary>
    /// The outcome tool is <em>added</em> to the agent's tool set, not substituted for it — MAF unions run-level
    /// tools with the agent's own. Red if the run options were built by replacing rather than adding, which would
    /// silently strip a constrained node's real tools.
    /// </summary>
    [Fact]
    public async Task Required_outcome_adds_to_the_agents_tools_rather_than_replacing_them()
    {
        var f = new RuntimeFixture().WithTool(Echo()).Build();
        f.Client.ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = Approval }, default);

        ToolNamesSeenByTheModel(f.Client).Should().BeEquivalentTo(["t__echo", "workflow__report_outcome"]);
    }

    /// <summary>
    /// The outcome tool belongs to the turn, not to the cached agent. Red if it were ever attached to the agent's
    /// own <c>ChatOptions</c>: the second turn, made on the same cached agent without an outcome schema, would
    /// still see it.
    /// </summary>
    [Fact]
    public async Task Outcome_tool_does_not_leak_into_the_next_turn_of_the_same_agent()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("one").ThenText("two");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = Approval }, default);
        await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "again", RuntimeFixture.User()), default);

        (f.Client.Requests[0].Options?.Tools ?? []).Select(t => t.Name).Should().Equal("workflow__report_outcome");
        (f.Client.Requests[1].Options?.Tools).Should().BeNullOrEmpty();
    }

    /// <summary>
    /// The case the whole design rests on: two turns of the <em>same cached agent</em> declaring <em>different</em>
    /// outcome sets. The agent cache is keyed on the definition alone, so anything that attached the tool to the
    /// agent — or that built the run options once and reused them — would serve the second turn the first turn's
    /// enum, silently offering a node a set of outcomes its process never declared. Red if the options are cached,
    /// reused, or hung off the agent: the second request would carry "approved"/"rejected" rather than
    /// "escalate"/"resolve".
    /// </summary>
    [Fact]
    public async Task Two_turns_of_one_agent_each_see_only_their_own_outcome_set()
    {
        var triage = new OutcomeToolSchema("workflow__report_outcome", ["escalate", "resolve"]);
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("one").ThenText("two");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = Approval }, default);
        await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "again", RuntimeFixture.User()) { RequiredOutcome = triage }, default);

        EnumOfferedIn(f.Client, 0).Should().Equal("approved", "rejected");
        EnumOfferedIn(f.Client, 1).Should().Equal("escalate", "resolve");
    }

    /// <summary>The <c>enum</c> array of the one outcome tool offered on request <paramref name="index"/>; throws if it was not offered at all.</summary>
    private static IReadOnlyList<string?> EnumOfferedIn(Thalos.Testing.ScriptedChatClient client, int index) =>
        [.. (client.Requests[index].Options?.Tools ?? [])
            .OfType<AIFunctionDeclaration>()
            .Single(t => string.Equals(t.Name, "workflow__report_outcome", StringComparison.Ordinal))
            .JsonSchema.GetProperty("properties").GetProperty(OutcomeToolSchema.ArgumentName)
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString())];

    /// <summary>
    /// Documents the decision on requirement 3: the turn is left free rather than forced with
    /// <c>ChatToolMode.RequireSpecific</c>, which would demand the report on the model's <em>next</em> message —
    /// before the node has done its work — rather than before the turn ends. Red if a tool mode is set.
    /// </summary>
    [Fact]
    public async Task Required_outcome_does_not_force_the_tool_choice()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = Approval }, default);

        // Parenthesised: unwrapped, a null Options would short-circuit the whole expression and assert nothing at all.
        (f.Client.Requests[0].Options?.ToolMode).Should().BeNull();
    }

    /// <summary>
    /// The full round trip the workflow engine depends on: the model calls the offered tool and the value comes back
    /// on <see cref="AgentTurnResult.ToolCalls"/>, shaped exactly as the read side parses it. Red if the tool is not
    /// offered (MAF answers an unknown-tool error instead of invoking it) or if it is not wrapped for recording.
    /// </summary>
    [Fact]
    public async Task Reported_outcome_arrives_on_the_turn_results_tool_calls()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenToolCall("workflow__report_outcome", new { outcome = "approved" }).ThenText("all done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = Approval }, default);

        r.IsSuccess.Should().BeTrue();
        var call = r.Value.ToolCalls.Should().ContainSingle().Which;
        call.ToolName.Should().Be("workflow__report_outcome");
        call.Succeeded.Should().BeTrue();
        call.ResultPreview.Should().Be("Outcome 'approved' recorded.");
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        arguments.RootElement.GetProperty(OutcomeToolSchema.ArgumentName).GetString().Should().Be("approved");
    }

    /// <summary>
    /// A schema no provider could accept fails the turn before anything is spent. Red if <c>BuildRunOptions</c>
    /// stops propagating the factory's failure, or is moved after the agent build and the model call.
    /// </summary>
    [Fact]
    public async Task Unusable_outcome_schema_fails_the_turn_without_calling_the_model()
    {
        var f = new RuntimeFixture().Build();
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(s, "go", RuntimeFixture.User()) { RequiredOutcome = new OutcomeToolSchema("bad name", ["a"]) }, default);

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.Validation);
        f.Client.Requests.Should().BeEmpty();
        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
    }
}
