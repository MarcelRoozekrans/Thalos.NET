using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Runtime;
using Thalos.Tests.Unit.Runtime;
using Thalos.Tools;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Workspaces;

/// <summary>
/// The workspace tools as the reflective <see cref="LocalToolSource"/> over <see cref="WorkspaceTools"/> offers them,
/// the source the remote run schemas are still built from, captured before that path moves to typed binders: the
/// <see cref="ToolOracle"/> dump, and the result text of a call to every tool, over caller text that the encoder
/// escapes. Characterization tests: they pass on today's code.
/// </summary>
public sealed class WorkspaceToolGoldenTests : IDisposable
{
    private const string Tricky = "<a & 'é' 🚀>";

    private static readonly Guid RunId = new(0x6b29fc40, 0xca47, 0x1067, 0xb3, 0x1d, 0x00, 0xdd, 0x01, 0x06, 0x62, 0xda);

    private readonly string _root = Directory.CreateTempSubdirectory("thalos-workspace-golden-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Red: in LocalToolSourceBinding.Options, set Description = name; every description in the dump changes.
    [Fact]
    public async Task The_reflective_workspace_tools_dump_as_before()
    {
        await using var services = Services();
        var dump = ToolOracle.Dump(await ToolsAsync(services));

        dump.Should().Be("""
            read_file | Read a text file from the run's workspace. The path is relative to the workspace root. | {"type":"object","properties":{"path":{"description":"Path relative to the workspace root.","type":"string"}},"required":["path"]} | {"type":"string"}
            list_files | List files and directories in the run's workspace, recursively. Directories are shown with a trailing '/'. | {"type":"object","properties":{"directory":{"description":"Directory relative to the workspace root; omit for the whole workspace.","type":["string","null"],"default":null}}} | {"type":"string"}
            write_file | Create or overwrite a text file in the run's workspace, creating parent directories as needed. Refused for a protected path or a file extension this run is not allowed to write. | {"type":"object","properties":{"path":{"description":"Path relative to the workspace root.","type":"string"},"content":{"description":"The file\u0027s new full content.","type":"string"}},"required":["path","content"]} | {"type":"string"}
            edit_file | Replace one exact occurrence of oldText with newText in a file already in the run's workspace. Refused unless oldText occurs exactly once, for a protected path, or a file extension this run is not allowed to write. | {"type":"object","properties":{"path":{"description":"Path relative to the workspace root.","type":"string"},"oldText":{"description":"The exact text to replace. Must occur exactly once in the file.","type":"string"},"newText":{"description":"The replacement text.","type":"string"}},"required":["path","oldText","newText"]} | {"type":"string"}
            """);
    }

    // Red: in LocalToolSourceBinding.Options, default SerializerOptions to a copy of AIJsonUtilities.DefaultOptions
    // with another Encoder; the escaping of the caller text in the results changes.
    [Fact]
    public async Task Every_workspace_tool_answers_as_before()
    {
        await using var services = Services();
        var text = await AnswersAsync(await ToolsAsync(services));

        text.Should().Be("""
            write_file: "wrote 28 bytes to 'notes.md'."
            read_file: "<a & 'é' \uD83D\uDE80>\nsecond line\n"
            edit_file: "edited 'notes.md'."
            list_files: "notes.md"
            write_file refused: "error: extension '.exe' is not writable in this run; allowed: .md"
            """);
    }

    [Fact]
    public async Task Workspace_typed_tools_match_the_reflective_oracle()
    {
        await using var services = Services(typed: true);
        var registered = services.GetServices<IToolSource>().Single(s => string.Equals(s.Name, RunWorkspaceToolOptions.SourceName, StringComparison.Ordinal));
        var typed = (await registered.GetToolsAsync(CancellationToken.None)).Value;
        var reflective = await ToolsAsync(services);

        // Every failing assertion is reported, so each Red below shows on its own line.
        using var scope = new AssertionScope();

        // Red: bind UseRunWorkspaceTools' tools in another order, write_file before list_files; the dump lines change places.
        ToolOracle.Dump(typed).Should().Be(ToolOracle.Dump(reflective));

        var typedAnswers = await AnswersAsync(typed);
        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
        var reflectiveAnswers = await AnswersAsync(reflective);

        // Red: build RunWorkspaceToolOptions.SerializerOptions from WorkspaceToolsJsonContext.Default.Options instead
        // of LocalToolSources.CreateOptions; its encoder escapes the file text, so the results differ.
        typedAnswers.Should().Be(reflectiveAnswers);

        // Red: write plain text instead of Tricky; the results no longer carry the characters the encoder decides on.
        reflectiveAnswers.Should().Contain("<a & 'é' \\uD83D\\uDE80>");
    }

    /// <summary>A call to every tool, in an empty workspace, over <see cref="Tricky"/>.</summary>
    private static async Task<string> AnswersAsync(IReadOnlyList<AITool> list)
    {
        var tools = list.Cast<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        using var turn = TurnScope.Begin(SessionId.New(), TurnId.New(), Caller());

        string[] results =
        [
            "write_file: " + ToolOracle.Result(await tools["write_file"].InvokeAsync(Args(("path", "notes.md"), ("content", Tricky + "\nsecond line\n")))),
            "read_file: " + ToolOracle.Result(await tools["read_file"].InvokeAsync(Args(("path", "notes.md")))),
            "edit_file: " + ToolOracle.Result(await tools["edit_file"].InvokeAsync(Args(("path", "notes.md"), ("oldText", "second"), ("newText", "2nd 'é'")))),
            "list_files: " + ToolOracle.Result(await tools["list_files"].InvokeAsync(Args())),
            "write_file refused: " + ToolOracle.Result(await tools["write_file"].InvokeAsync(Args(("path", "run.exe"), ("content", Tricky)))),
        ];
        return string.Join('\n', results);
    }

    /// <summary>The golden's container; <paramref name="typed"/> registers the options through <c>UseRunWorkspaceTools</c>, with its typed source.</summary>
    private ServiceProvider Services(bool typed = false)
    {
        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", _root, null);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRunWorkspaceProvider>(new OneWorkspace(workspace));
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" };
        if (typed)
        {
            services.AddThalos(t => t.UseRunWorkspaceTools(extensions));
        }
        else
        {
            services.AddSingleton(new RunWorkspaceToolOptions { AllowedWriteExtensions = extensions });
        }

        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlyList<AITool>> ToolsAsync(IServiceProvider services) =>
        (await new LocalToolSource(RunWorkspaceToolOptions.SourceName, services, [typeof(WorkspaceTools)]).GetToolsAsync(CancellationToken.None)).Value;

    private static TestSecurityContext Caller() =>
        new("run-caller") { Claims = new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = RunId.ToString() } };

    private static AIFunctionArguments Args(params (string Name, object? Value)[] arguments)
    {
        var result = new AIFunctionArguments(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            result[name] = value;
        }

        return result;
    }

    private sealed class OneWorkspace(RunWorkspace workspace) : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException("The golden only finds its one workspace.");

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => new(runId == workspace.RunId ? workspace : null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => new([workspace]);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => new(UnitResult<AgentError>.Success());
    }
}
