using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Thalos.Mcp;
using Thalos.Runtime;
using Thalos.Sandbox;
using Thalos.Tools;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// The sandbox tools through today's reflective sources, captured before they move to typed binders: the
/// <c>workspace</c> and <c>sandbox</c> remote run sources as <see cref="SandboxThalosBuilderExtensions.UseSandboxRunWorkspaces"/>
/// registers them, and <see cref="SandboxTools"/> under the reflective <see cref="LocalToolSource"/> the remote schemas
/// are built from, with the result text of a call to each of its tools. Characterization tests: they pass on today's code.
/// </summary>
public sealed class RemoteRunToolGoldenTests : IDisposable
{
    private static readonly Guid RunId = new(0x6b29fc40, 0xca47, 0x1067, 0xb3, 0x1d, 0x00, 0xdd, 0x01, 0x06, 0x62, 0xda);

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("thalos-remote-golden-").FullName;

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    // Red: in LocalToolSourceBinding.Options, set Description = name; the schemas the remote sources take from the
    // reflective source change, and so does the dump.
    [Fact]
    public async Task The_remote_run_sources_dump_as_before()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(t => t.UseSandboxRunWorkspaces(o =>
        {
            o.DataRoot = _dataRoot;
            o.Image = "thalos-sandbox:test";
        }));
        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IToolSource>().OfType<RemoteRunToolSource>().ToDictionary(s => s.Name, StringComparer.Ordinal);

        var workspace = ToolOracle.Dump((await sources[RunWorkspaceToolOptions.SourceName].GetToolsAsync(CancellationToken.None)).Value);
        var sandbox = ToolOracle.Dump((await sources[SandboxToolOptions.SourceName].GetToolsAsync(CancellationToken.None)).Value);
        var dump = $"{RunWorkspaceToolOptions.SourceName}:\n{workspace}\n{SandboxToolOptions.SourceName}:\n{sandbox}";

        dump.Should().Be("""
            workspace:
            read_file | Read a text file from the run's workspace. The path is relative to the workspace root. | {"type":"object","properties":{"path":{"description":"Path relative to the workspace root.","type":"string"}},"required":["path"]} | {"type":"string"}
            list_files | List files and directories in the run's workspace, recursively. Directories are shown with a trailing '/'. | {"type":"object","properties":{"directory":{"description":"Directory relative to the workspace root; omit for the whole workspace.","type":["string","null"],"default":null}}} | {"type":"string"}
            write_file | Create or overwrite a text file in the run's workspace, creating parent directories as needed. Refused for a protected path or a file extension this run is not allowed to write. | {"type":"object","properties":{"path":{"description":"Path relative to the workspace root.","type":"string"},"content":{"description":"The file\u0027s new full content.","type":"string"}},"required":["path","content"]} | {"type":"string"}
            edit_file | Replace one exact occurrence of oldText with newText in a file already in the run's workspace. Refused unless oldText occurs exactly once, for a protected path, or a file extension this run is not allowed to write. | {"type":"object","properties":{"path":{"description":"Path relative to the workspace root.","type":"string"},"oldText":{"description":"The exact text to replace. Must occur exactly once in the file.","type":"string"},"newText":{"description":"The replacement text.","type":"string"}},"required":["path","oldText","newText"]} | {"type":"string"}
            sandbox:
            build | Build the run's solution with `dotnet build`. Returns the exit code, a summary and the tail of the output. | {"type":"object","properties":{}} | {"type":"string"}
            test | Run the run's tests with `dotnet test`, optionally filtered. Returns the exit code, a pass/fail summary and the tail of the output. | {"type":"object","properties":{"filter":{"description":"Optional \u0060dotnet test --filter\u0060 expression, e.g. \u0060FullyQualifiedName~Orders\u0060.","type":["string","null"],"default":null}}} | {"type":"string"}
            """);
    }

    // Red: in LocalToolSourceBinding.Options, default SerializerOptions to a copy of AIJsonUtilities.DefaultOptions
    // with another Encoder; the escaping of the process output in the results changes.
    // Red: rename SandboxTools.Test's filter parameter to filterX; the "filter" argument then no longer binds and the
    // recorded test arguments lose --filter Name~Orders.
    [Fact]
    public async Task Every_sandbox_tool_answers_as_before()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<IRunWorkspaceProvider>(new OneWorkspace(new RunWorkspace(RunId, "repo", "remote", "main", "branch", "/work", "/work/app.slnx")));
        var runner = new FixedRunner(new ProcessOutcome(1, false, "error CS0103: <a & 'é' 🚀>\nBuild FAILED.\n", ErrorLineCount: 1));
        services.AddSingleton<ISandboxProcessRunner>(runner);
        services.AddSingleton(new SandboxToolOptions());
        await using var provider = services.BuildServiceProvider();
        var tools = (await new LocalToolSource(SandboxToolOptions.SourceName, provider, [typeof(SandboxTools)]).GetToolsAsync(CancellationToken.None)).Value
            .Cast<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        using var turn = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("run", new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = RunId.ToString("D") }));

        var build = ToolOracle.Result(await tools["build"].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)));
        var test = ToolOracle.Result(await tools["test"].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["filter"] = "Name~Orders" }));
        var text = $"build: {build}\nbuild args: {string.Join(' ', runner.Specs[0].Arguments)}\ntest: {test}\ntest args: {string.Join(' ', runner.Specs[1].Arguments)}";

        text.Should().Be("""
            build: "exit: 1\nerrors: 1\n--- output (last 44 bytes) ---\nerror CS0103: <a & 'é' \uD83D\uDE80>\nBuild FAILED.\n"
            build args: build /work/app.slnx --nologo -v:q -clp:ErrorsOnly
            test: "exit: 1\n(no test summary found)\n--- output (last 44 bytes) ---\nerror CS0103: <a & 'é' \uD83D\uDE80>\nBuild FAILED.\n"
            test args: test /work/app.slnx --nologo -v:q --filter Name~Orders
            """);
    }

    private sealed class OneWorkspace(RunWorkspace workspace) : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException("The golden only finds its one workspace.");

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => new(runId == workspace.RunId ? workspace : null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => new([workspace]);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => new(UnitResult<AgentError>.Success());
    }

    /// <summary>Answers every process with one outcome and records each spec, so the golden shows what the arguments bound to.</summary>
    private sealed class FixedRunner(ProcessOutcome outcome) : ISandboxProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            return Task.FromResult(outcome);
        }
    }
}
