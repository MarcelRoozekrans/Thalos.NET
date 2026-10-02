using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Sandbox;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

public sealed class SandboxToolsTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private readonly RecordingRunner _runner = new();
    private readonly FakeWorkspaces _workspaces = new();
    private readonly SandboxToolOptions _options = new() { OutputTailBytes = 1024 };

    private SandboxTools Tools(string? solution = "/work/app.slnx")
    {
        var workspace = new RunWorkspace(RunId, "repo", "remote", "main", "branch", "/work", solution);
        _workspaces.Workspace = workspace;
        return new SandboxTools(_workspaces, _runner, _options, NullLogger<SandboxTools>.Instance);
    }

    private static readonly Dictionary<string, string> NoClaims = new(StringComparer.Ordinal);

    private static TestCaller Caller() => new(new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = RunId.ToString("D") });

    /// <summary>Red: change any of the fixed arguments, the working directory or the timeout in SandboxTools.Build, or fall back to the root when a solution path is set.</summary>
    [Fact]
    public async Task Build_runs_dotnet_build_on_the_solution_with_fixed_arguments()
    {
        _runner.Next = new ProcessOutcome(0, false, "");

        await Tools().Build(Caller());

        var spec = _runner.Specs.Should().ContainSingle().Subject;
        spec.FileName.Should().Be("dotnet");
        spec.Arguments.Should().Equal("build", "/work/app.slnx", "--nologo", "-v:q", "-clp:ErrorsOnly");
        spec.WorkingDirectory.Should().Be("/work");
        spec.Timeout.Should().Be(_options.BuildTimeout);
    }

    /// <summary>Red: in SandboxTools.Build use workspace.Root instead of the solution-or-root target, or drop the fallback to the root.</summary>
    [Fact]
    public async Task Build_falls_back_to_the_workspace_root_without_a_solution()
    {
        _runner.Next = new ProcessOutcome(0, false, "");

        await Tools(solution: null).Build(Caller());

        _runner.Specs.Single().Arguments[1].Should().Be("/work");
    }

    /// <summary>Red: build the filter into one shell-style string, e.g. add "--filter " + filter as a single argument, or drop the "--filter" entry.</summary>
    [Fact]
    public async Task Test_passes_a_valid_filter_as_one_argument()
    {
        _runner.Next = new ProcessOutcome(0, false, "");

        await Tools().Test(Caller(), "FullyQualifiedName~Orders & Name!=X");

        var spec = _runner.Specs.Should().ContainSingle().Subject;
        spec.Arguments.Should().Equal("test", "/work/app.slnx", "--nologo", "-v:q", "--filter", "FullyQualifiedName~Orders & Name!=X");
        spec.Timeout.Should().Be(_options.TestTimeout);
    }

    /// <summary>Red: add an unconditional "--filter" entry in SandboxTools.Test.</summary>
    [Fact]
    public async Task Test_without_a_filter_adds_no_filter_argument()
    {
        _runner.Next = new ProcessOutcome(0, false, "");

        await Tools().Test(Caller());

        _runner.Specs.Single().Arguments.Should().Equal("test", "/work/app.slnx", "--nologo", "-v:q");
    }

    /// <summary>
    /// S-argument-injection. Red: drop the regex from SandboxTools.IsValidFilter, which lets "--logger x", "a;b" and "$(x)"
    /// through and turns the theory red; separately, drop the StartsWith('-') check, which turns "-p:X=1" red because
    /// it matches the character class; and change the pattern's \z back to $, which turns the trailing-newline case red.
    /// </summary>
    [Theory]
    [InlineData("--logger x")]
    [InlineData("-p:X=1")]
    [InlineData("a;b")]
    [InlineData("$(x)")]
    [InlineData("")]
    [InlineData("Name=a\n")]
    public async Task Test_refuses_a_filter_that_injects_an_option(string filter)
    {
        var result = await Tools().Test(Caller(), filter);

        result.Should().Be("error: filter refused");
        _runner.Specs.Should().BeEmpty();
    }

    /// <summary>Red: raise or drop the 256-character bound in the filter regex.</summary>
    [Fact]
    public async Task Test_refuses_a_filter_of_300_characters_and_accepts_256()
    {
        (await Tools().Test(Caller(), new string('a', 300))).Should().Be("error: filter refused");
        _runner.Specs.Should().BeEmpty();

        _runner.Next = new ProcessOutcome(0, false, "");
        (await Tools().Test(Caller(), new string('a', 256))).Should().StartWith("exit: 0");
    }

    /// <summary>Red: in SandboxTools.FindWorkspaceAsync look the workspace up with a fallback run id instead of requiring the caller's run claim, as in `RunIdOf(caller) ?? knownId`.</summary>
    [Fact]
    public async Task A_caller_without_a_run_claim_is_refused()
    {
        var tools = Tools();

        (await tools.Build(new TestCaller(NoClaims))).Should().StartWith("error:");
        (await tools.Test(new TestCaller(NoClaims))).Should().StartWith("error:");
        _runner.Specs.Should().BeEmpty();
    }

    /// <summary>Red: in SandboxTools.Build and Test drop the null check on the found workspace, so a missing one reaches the runner or throws.</summary>
    [Fact]
    public async Task A_run_with_no_workspace_is_refused()
    {
        var tools = Tools();
        _workspaces.Workspace = null;

        (await tools.Build(Caller())).Should().StartWith("error:");
        (await tools.Test(Caller())).Should().StartWith("error:");
        _runner.Specs.Should().BeEmpty();
    }

    /// <summary>Red: drop the StartError branch in SandboxTools.Format, which then fails on the null exit code.</summary>
    [Fact]
    public async Task A_process_that_cannot_start_is_reported_not_thrown()
    {
        _runner.Next = new ProcessOutcome(null, false, "", StartError: "No such file");

        (await Tools().Build(Caller())).Should().Be("error: could not start dotnet: No such file");
        (await Tools().Test(Caller())).Should().Be("error: could not start dotnet: No such file");
    }

    /// <summary>Red: compute the test summary from the tailed output instead of the full output.</summary>
    [Fact]
    public async Task The_summary_is_found_before_the_output_is_tailed()
    {
        var output = "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3\n" + new string('x', 100 * 1024) + "\n";
        _runner.Next = new ProcessOutcome(0, false, output);

        var result = await Tools().Test(Caller());

        var lines = result.Split('\n');
        lines[0].Should().Be("exit: 0");
        lines[1].Should().Be("Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3");
        result.Should().NotContain("Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3\n" + "x");
        Encoding.UTF8.GetByteCount(result).Should().BeLessThan(2048);
    }

    /// <summary>Red: return the whole output instead of the last OutputTailBytes bytes, or the first bytes instead of the last.</summary>
    [Fact]
    public async Task The_tail_is_the_end_of_the_output()
    {
        _runner.Next = new ProcessOutcome(1, false, "HEAD" + new string('x', 5000) + "TAIL");

        var result = await Tools().Test(Caller());

        result.Should().EndWith("TAIL").And.NotContain("HEAD");
        result.Should().Contain("--- output (last 1024 bytes) ---");
    }

    /// <summary>Red: report the exit code of a timed out outcome, or drop the TimedOut branch in SandboxTools.Format.</summary>
    [Fact]
    public async Task A_timeout_is_reported_as_such()
    {
        _options.TestTimeout = TimeSpan.FromSeconds(90);
        _runner.Next = new ProcessOutcome(null, true, "partial");

        var result = await Tools().Test(Caller());

        result.Should().StartWith("exit: timed out after 00:01:30\n");
        result.Should().EndWith("partial");
    }

    /// <summary>
    /// The copy and the run share one deadline: a copy that took three minutes of a ten-minute budget leaves the run seven.
    /// The fake clock moves three minutes between the call's start and the copy's end. Red: give the run the full
    /// BuildTimeout in SandboxTools.RunAsync instead of the remainder.
    /// </summary>
    [Fact]
    public async Task The_copy_and_the_build_share_one_deadline()
    {
        var root = Directory.CreateTempSubdirectory("thalos-deadline-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "app.slnx"), "<Solution />");
            _workspaces.Workspace = new RunWorkspace(RunId, "repo", "remote", "main", "branch", root, Path.Combine(root, "app.slnx"));
            var options = new SandboxToolOptions { BuildTimeout = TimeSpan.FromMinutes(10), ScratchRoot = Path.Combine(root, "..", Path.GetFileName(root) + "-scratch") };
            var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMinutes(3) };
            _runner.Next = new ProcessOutcome(0, false, "");

            await new SandboxTools(_workspaces, _runner, options, NullLogger<SandboxTools>.Instance, clock).Build(Caller());

            _runner.Specs.Should().ContainSingle().Which.Timeout.Should().Be(TimeSpan.FromMinutes(7));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Red: stop using outcome.ErrorLineCount for the build summary, e.g. hardcode 0.</summary>
    [Fact]
    public async Task The_build_summary_counts_error_lines()
    {
        _runner.Next = new ProcessOutcome(1, false, "", ErrorLineCount: 2);

        var result = await Tools().Build(Caller());

        result.Split('\n')[1].Should().Be("errors: 2");
    }

    private sealed class FakeWorkspaces : IRunWorkspaceProvider
    {
        public RunWorkspace? Workspace { get; set; }

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => new(Workspace);

        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingRunner : ISandboxProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public ProcessOutcome Next { get; set; } = new(0, false, "");

        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            return Task.FromResult(Next);
        }
    }

    private sealed class TestCaller(IReadOnlyDictionary<string, string> claims) : ISecurityContext
    {
        public string Id => "test";

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Claims { get; } = claims;
    }
}
