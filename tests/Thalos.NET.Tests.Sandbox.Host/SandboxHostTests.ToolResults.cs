using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos.Runtime;
using Thalos.Sandbox;
using Thalos.Tools;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox.Host;

public sealed partial class SandboxHostTests
{
    private static readonly Guid ToolRunId = new(0x6b29fc40, 0xca47, 0x1067, 0xb3, 0x1d, 0x00, 0xdd, 0x01, 0x06, 0x62, 0xda);

    /// <summary>
    /// The host's typed <c>sandbox</c> source, built with <see cref="SandboxToolOptions.SerializerOptions"/>, against the
    /// reflective <see cref="LocalToolSource"/> over <see cref="SandboxTools"/> in the same container: the
    /// <see cref="ToolOracle"/> dump, and the result text of a call to each tool, over process output the encoder escapes.
    /// </summary>
    [Fact]
    public async Task Sandbox_typed_tools_match_the_reflective_oracle()
    {
        var runner = new FixedRunner(new ProcessOutcome(1, false, "error CS0103: <a & 'é' 🚀>\nBuild FAILED.\n", ErrorLineCount: 1));
        await using var host = await HostHarness.StartAsync(WorkRoot(), services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IRunWorkspaceProvider>(new OneWorkspace(new RunWorkspace(ToolRunId, "repo", "remote", "main", "branch", "/work", "/work/app.slnx"))));
            s.Replace(ServiceDescriptor.Singleton<ISandboxProcessRunner>(runner));
            s.Replace(ServiceDescriptor.Singleton(new SandboxToolOptions()));
        });
        var registered = host.Services.GetServices<IToolSource>().Single(s => string.Equals(s.Name, SandboxToolOptions.SourceName, StringComparison.Ordinal));
        var typed = (await registered.GetToolsAsync(CancellationToken.None)).Value;
        var reflective = (await new LocalToolSource(SandboxToolOptions.SourceName, host.Services, [typeof(SandboxTools)]).GetToolsAsync(CancellationToken.None)).Value;

        // Every failing assertion is reported, so each Red below shows on its own line.
        using var scope = new AssertionScope();

        // Red: bind the host's sandbox tools in another order, Test before Build; the dump lines change places.
        ToolOracle.Dump(typed).Should().Be(ToolOracle.Dump(reflective));

        var typedAnswers = await SandboxAnswersAsync(typed);
        var reflectiveAnswers = await SandboxAnswersAsync(reflective);

        // Red: build SandboxToolOptions.SerializerOptions from SandboxToolsJsonContext.Default.Options instead of
        // LocalToolSources.CreateOptions; its encoder escapes the process output, so the results differ.
        typedAnswers.Should().Be(reflectiveAnswers);

        // Red: answer with plain process output; the results no longer carry the characters the encoder decides on.
        reflectiveAnswers.Should().Contain("<a & 'é' \\uD83D\\uDE80>");
    }

    private static async Task<string> SandboxAnswersAsync(IReadOnlyList<AITool> list)
    {
        var tools = list.Cast<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        using var turn = TurnScope.Begin(SessionId.New(), TurnId.New(), new RunCaller(new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = ToolRunId.ToString("D") }));
        var build = ToolOracle.Result(await tools["build"].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)));
        var test = ToolOracle.Result(await tools["test"].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["filter"] = "Name~Orders" }));
        return $"build: {build}\ntest: {test}";
    }

    private sealed class RunCaller(IReadOnlyDictionary<string, string> claims) : ISecurityContext
    {
        public string Id => "run";

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Claims { get; } = claims;
    }

    private sealed class OneWorkspace(RunWorkspace workspace) : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException("The guard only finds its one workspace.");

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => new(runId == workspace.RunId ? workspace : null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => new([workspace]);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => new(UnitResult<AgentError>.Success());
    }

    private sealed class FixedRunner(ProcessOutcome outcome) : ISandboxProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct) => Task.FromResult(outcome);
    }
}
