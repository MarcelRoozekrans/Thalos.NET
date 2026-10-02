using AwesomeAssertions.Execution;
using Docker.DotNet.Models;
using Thalos.Sandbox;
using Thalos.Sandbox.Docker;
using Xunit.Abstractions;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>
/// Confinement end to end: the real Docker runtime, the real sandbox image and the real provider stack, as
/// <see cref="SandboxEndToEndFixture"/> sets them up. Every test runs its own run, from create to remove, through the
/// tools an agent would call. Every test skips without a Docker engine.
/// </summary>
public sealed class SandboxEndToEndTests(SandboxEndToEndFixture fixture, ITestOutputHelper output) : IClassFixture<SandboxEndToEndFixture>, IDisposable
{
    /// <summary>The spike's vector 1: a target an agent writes, which MSBuild runs before the compiler.</summary>
    private const string MarkerProps = """
        <Project>
          <Target Name="M" BeforeTargets="CoreCompile">
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)marker.txt" Lines="ran" Overwrite="true" />
          </Target>
        </Project>
        """;

    /// <summary>
    /// S3. Red: run the sandbox host in-process on the test machine instead of in the container, as A10's fake runtime
    /// does; Roslyn's load then runs the target on the host, and check 2 finds the marker under the temp root.
    /// Red 2: skip the Roslyn call; the target never runs and check 1 reads no marker.
    /// </summary>
    [SkippableFact]
    public async Task An_agent_written_build_target_runs_only_inside_the_container()
    {
        Skip.IfNot(DockerAvailable.Value);
        await using var run = await fixture.StartRunAsync("s3");

        var written = await run.CallAsync("workspace", "write_file", ("path", "Directory.Build.props"), ("content", MarkerProps));
        var diagnostics = await run.CallAsync("roslyn", "get_diagnostics");
        var marker = await run.CallAsync("workspace", "read_file", ("path", "marker.txt"));
        var onHost = SandboxEndToEndFixture.FindFiles(fixture.TempRoot, "marker.txt");

        using var _ = new AssertionScope();
        written.Should().NotStartWith("error", "the write is allowed");
        diagnostics.Should().NotStartWith("error", "the call forces Roslyn to reload the written props");
        marker.Trim().Should().Be("ran", "the target ran inside the container");
        onHost.Should().BeEmpty("MSBuild never runs on the host (S3); the temp root holds the data root, the mirror and the publish worktrees");
    }

    /// <summary>
    /// Red: set HTTPS_PROXY in the spec's environment to a dead address; restore fails, Newtonsoft.Json is unresolved and
    /// Roslyn reports CS0246. Red 2: drop the CS0219 line from the seed's A.cs; the diagnostics then hold no CS0219.
    /// </summary>
    [SkippableFact]
    public async Task Restore_runs_through_the_proxy_so_no_package_is_unresolved()
    {
        Skip.IfNot(DockerAvailable.Value);
        await using var run = await fixture.StartRunAsync("restore");

        var readiness = await fixture.Provider.ReadinessAsync(run.RunId, CancellationToken.None);
        var diagnostics = await run.CallAsync("roslyn", "get_diagnostics");

        using var _ = new AssertionScope();
        readiness.IsSuccess.Should().BeTrue(readiness.IsFailure ? readiness.Error.ToString() : "");
        readiness.Value.Restore.Should().Be("ok", readiness.Value.RestoreDetail);
        diagnostics.Should().Contain("CS0219", "the diagnostics are really computed: the seed has one deliberate warning");
        diagnostics.Should().NotContain("CS0246", "Newtonsoft.Json resolves");
        diagnostics.Should().NotContain("NU1101");
        diagnostics.Should().NotContain("NU1301");
    }

    /// <summary>Red: in SandboxTools.Build, pass a target that does not exist; the image built from it answers exit 1.</summary>
    [SkippableFact]
    public async Task Build_and_test_run_in_the_container()
    {
        Skip.IfNot(DockerAvailable.Value);
        await using var run = await fixture.StartRunAsync("build");

        var build = await run.CallAsync("sandbox", "build");
        var test = await run.CallAsync("sandbox", "test");

        using var _ = new AssertionScope();
        build.Should().StartWith("exit: 0\n");
        test.Should().StartWith("exit: 0\n");
        test.Should().Contain("Passed!").And.Contain("Passed:     1");
    }

    /// <summary>
    /// S1. The test process holds <c>GITHUB_TOKEN</c> while the sandbox is created; a test the agent writes checks the
    /// container's own environment, the host process's <c>/proc/1/environ</c>, and its own. The test's child environment
    /// is curated and would drop the variable anyway, so the container's environment is the check that can fail.
    /// Red: in DockerSandboxRuntime.ContainerParameters, add this process's GITHUB_TOKEN to Env, as an inherited
    /// environment would; the container's environment then holds it, the written test fails and sandbox__test exits 1.
    /// </summary>
    [SkippableFact]
    public async Task The_container_never_sees_a_host_secret()
    {
        Skip.IfNot(DockerAvailable.Value);
        const string secret = "ghp_e2e_host_secret_never_in_a_sandbox";
        var before = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", secret);
        EndToEndRun run;
        try
        {
            run = await fixture.StartRunAsync("s1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", before);
        }

        await using (run)
        {
            var written = await run.CallAsync("workspace", "write_file", ("path", "Lib.Tests/SecretTests.cs"), ("content", """
                namespace Lib.Tests;

                public class SecretTests
                {
                    [Xunit.Fact]
                    public void No_host_secret()
                    {
                        Xunit.Assert.Null(System.Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
                        var container = System.IO.File.ReadAllText("/proc/1/environ");
                        Xunit.Assert.Contains("THALOS_SANDBOX_RUN_ID=", container);
                        Xunit.Assert.DoesNotContain("GITHUB_TOKEN", container);
                    }
                }
                """));
            var test = await run.CallAsync("sandbox", "test", ("filter", "FullyQualifiedName~SecretTests"));

            using var _ = new AssertionScope();
            written.Should().NotStartWith("error");
            test.Should().StartWith("exit: 0\n", "the written test passes: no host secret is in the container");
            test.Should().Contain("Passed:     1", "the written test is the one that ran");
            test.Should().NotContain(secret);
        }
    }

    /// <summary>
    /// S5. A test the agent writes runs during sandbox__test and writes a workflow file straight into the worktree,
    /// around the workspace tools' protected-path refusal.
    /// Red: in SandboxRunWorkspaceProvider.ApplyToNewWorktreeAsync, pass an empty ProtectedPathSet instead of the
    /// options'; the checkout succeeds and the workflow file is staged for publishing.
    /// </summary>
    [SkippableFact]
    public async Task A_workflow_file_written_around_the_tools_is_refused_at_publish()
    {
        Skip.IfNot(DockerAvailable.Value);
        await using var run = await fixture.StartRunAsync("s5");

        var refused = await run.CallAsync("workspace", "write_file", ("path", ".github/workflows/x.yml"), ("content", "on: push\n"));
        var written = await run.CallAsync("workspace", "write_file", ("path", "Lib.Tests/WorkflowWriter.cs"), ("content", """
            namespace Lib.Tests;

            public class WorkflowWriter
            {
                [Xunit.Fact]
                public void Writes_a_workflow()
                {
                    System.IO.Directory.CreateDirectory("/work/repo/.github/workflows");
                    System.IO.File.WriteAllText("/work/repo/.github/workflows/x.yml", "on: push\njobs: {}\n");
                }
            }
            """));
        var test = await run.CallAsync("sandbox", "test", ("filter", "FullyQualifiedName~WorkflowWriter"));
        var parked = await fixture.Provider.ParkAsync(run.RunId, CancellationToken.None);
        var checkout = await fixture.Provider.CheckoutForPublishAsync(run.RunId, CancellationToken.None);

        using var _ = new AssertionScope();
        refused.Should().StartWith("error", "the workspace tools refuse a protected path; the publish side is the control");
        written.Should().NotStartWith("error");
        test.Should().StartWith("exit: 0\n").And.Contain("Passed:     1");
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        checkout.IsFailure.Should().BeTrue("a change to a protected path fails publish (S5)");
        checkout.Error.ToString().Should().Contain(".github/workflows/x.yml");
        Directory.Exists(Path.Combine(fixture.DataRoot, "publish", "runs", run.RunId.ToString())).Should().BeFalse("the refused worktree is removed");
    }

    /// <summary>
    /// Red: skip the runtime delete in the park's finish; the container is still there. Red 2: export against the
    /// sandbox's new commit rather than the base; the patch is empty and B.cs is not in the worktree.
    /// </summary>
    [SkippableFact]
    public async Task A_parked_run_has_no_container_and_its_change_publishes()
    {
        Skip.IfNot(DockerAvailable.Value);
        await using var run = await fixture.StartRunAsync("park");
        const string content = "namespace Lib;\n\npublic static class B\n{\n    public static int One() => 1;\n}\n";

        var written = await run.CallAsync("workspace", "write_file", ("path", "Lib/B.cs"), ("content", content));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var parked = await fixture.Provider.ParkAsync(run.RunId, CancellationToken.None);
        fixture.Timings.Enqueue($"park: {watch.Elapsed.TotalSeconds:F1} s");
        var handle = await fixture.Runtime.GetAsync(run.SandboxId, CancellationToken.None);
        var volumes = await fixture.Docker.Docker.Volumes.ListAsync(new VolumesListParameters
        {
            Filters = DockerSandboxFixture.Filter("name", DockerSandboxRuntime.VolumeName(run.SandboxId)),
        });
        var checkout = await fixture.Provider.CheckoutForPublishAsync(run.RunId, CancellationToken.None);

        using var _ = new AssertionScope();
        written.Should().NotStartWith("error");
        parked.IsSuccess.Should().BeTrue(parked.IsFailure ? parked.Error.ToString() : "");
        handle.Should().NotBeNull();
        handle!.State.Should().Be(SandboxState.Missing, "a parked run has no container");
        (volumes.Volumes ?? []).Should().BeEmpty("nor a work volume");
        checkout.IsSuccess.Should().BeTrue(checkout.IsFailure ? checkout.Error.ToString() : "");
        if (checkout.IsSuccess)
        {
            var root = checkout.Value.Root;
            File.ReadAllText(Path.Combine(root, "Lib", "B.cs")).ReplaceLineEndings("\n").Should().Be(content);
            Directory.Exists(Path.Combine(root, "Lib", "obj")).Should().BeFalse("build output never travels in the patch");
        }
    }

    public void Dispose()
    {
        foreach (var timing in fixture.Timings)
        {
            output.WriteLine(timing);
        }

        foreach (var line in fixture.Log.TakeLast(80))
        {
            output.WriteLine(line);
        }
    }
}
