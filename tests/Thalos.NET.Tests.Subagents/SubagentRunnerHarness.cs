using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Subagents;

/// <summary>Builds a <see cref="SubagentRunner"/> over a substituted <see cref="IAgentRuntime"/> and a controllable clock.</summary>
internal sealed class SubagentRunnerHarness
{
    /// <summary>The substituted runtime the runner under test delegates to; configure its returns per test.</summary>
    public IAgentRuntime Runtime { get; } = Substitute.For<IAgentRuntime>();

    /// <summary>Controllable clock passed to the runner; not advanced by <see cref="Build"/>, so tests own the timeline.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>Ceilings the runner under test is built with; mutate before calling <see cref="Build"/> to test a limit.</summary>
    public SubagentOptions Options { get; } = new();

    /// <summary>Creates a harness with a fresh mock runtime, clock and default options.</summary>
    public static SubagentRunnerHarness Create() => new();

    /// <summary>Builds the runner under test over this harness's <see cref="Runtime"/>, <see cref="Options"/> and <see cref="Time"/>.</summary>
    public ISubagentRunner Build() => new SubagentRunner(Runtime, Options, Time, logger: null);

    /// <summary>A minimal <see cref="SubagentRunRequest"/>; override only the fields a test cares about.</summary>
    public static SubagentRunRequest Request(
        AgentId? agentId = null, string task = "do the thing", int depth = 0, SubagentBudget? budget = null) =>
        new()
        {
            AgentId = agentId ?? AgentId.New(),
            Task = task,
            Caller = new StubCaller(),
            Depth = depth,
            Budget = budget ?? SubagentBudget.Default,
        };

    private sealed class StubCaller : ISecurityContext
    {
        public string Id => "schedule:test";
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "reader" };
        public IReadOnlyDictionary<string, string> Claims { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
