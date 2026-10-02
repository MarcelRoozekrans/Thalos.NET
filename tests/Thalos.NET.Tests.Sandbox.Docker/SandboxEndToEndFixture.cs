using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Thalos.Mcp;
using Thalos.Runtime;
using Thalos.Sandbox;
using Thalos.Sandbox.Docker;
using Thalos.Tools;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox.Docker;

/// <summary>
/// The end-to-end tests' world: the sandbox image built once from <c>Image/Dockerfile</c>, a seed repository that is a
/// real solution, and the full trusted-side provider stack, <see cref="DockerSandboxRuntime"/>,
/// <see cref="SandboxRunWorkspaceProvider"/> with its <see cref="GitMirrorStore"/>, and <see cref="RemoteRunToolSource"/>
/// for <c>workspace</c>, <c>sandbox</c> and <c>roslyn</c>. Every Docker object it makes, the image included, carries the
/// <see cref="DockerSandboxFixture"/>'s labels and is removed with them.
/// </summary>
public sealed class SandboxEndToEndFixture : IAsyncLifetime
{
    /// <summary>The shipped default protected paths, with <c>AGENT.md</c> as the standing instructions.</summary>
    public static readonly string[] ShippedProtectedPaths =
        [".git/", "AGENT.md", ".gitattributes", ".gitmodules", ".github/", ".gitlab-ci.yml", "azure-pipelines.yml", ".azure-pipelines/", ".circleci/", "Jenkinsfile"];

    private readonly ConcurrentQueue<string> log = new();
    private IReadOnlyList<AITool>? roslynSchemas;
    private ServiceProvider? services;

    /// <summary>Labels, networks and clean-up shared with the runtime tests.</summary>
    public DockerSandboxFixture Docker { get; } = new();

    /// <summary>The test's temp root: data root, seed repository and anything else the host side writes.</summary>
    public string TempRoot { get; } = Directory.CreateTempSubdirectory("thalos-sbx-e2e-").FullName;

    /// <summary>Trusted-side state: mirrors, records, stored patches, publish worktrees.</summary>
    public string DataRoot => Path.Combine(TempRoot, "data");

    /// <summary>The seed repository, bare, as its remote URL.</summary>
    public string Remote => Path.Combine(TempRoot, "seed.git");

    /// <summary>The sandbox image, tagged with this fixture's suffix.</summary>
    public string Image => $"thalos-sandbox-e2e:{Docker.Suffix}";

    /// <summary>How long building the image took.</summary>
    public TimeSpan ImageBuildTime { get; private set; }

    public DockerSandboxRuntime Runtime { get; private set; } = null!;

    public SandboxRunWorkspaceProvider Provider { get; private set; } = null!;

    /// <summary>The <c>roslyn</c> source: <see cref="RemoteRunToolSource.ForLocalSchemas"/> over schemas listed from a sandbox.</summary>
    public RemoteRunToolSource Roslyn { get; private set; } = null!;

    /// <summary>Everything the stack logged, in order.</summary>
    public IReadOnlyList<string> Log => [.. log];

    /// <summary>Measured timings, in the order taken, for the report.</summary>
    public ConcurrentQueue<string> Timings { get; } = new();

    public async Task InitializeAsync()
    {
        if (!DockerAvailable.Value)
        {
            return;
        }

        await Docker.InitializeAsync();
        var watch = Stopwatch.StartNew();
        await BuildImageAsync();
        ImageBuildTime = watch.Elapsed;
        Timings.Enqueue($"image build: {ImageBuildTime.TotalSeconds:F0} s");

        await SeedRepositoryAsync();
        Runtime = Docker.NewRuntime(Docker.Options());
        services = BuildServices();
        Provider = services.GetRequiredService<SandboxRunWorkspaceProvider>();
        Roslyn = RemoteRunToolSource.ForLocalSchemas(
            "roslyn",
            new SandboxListedSchemas(this),
            Provider,
            new RemoteRunToolOptions(),
            services.GetRequiredService<ILoggerFactory>(),
            TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        if (!DockerAvailable.Value)
        {
            return;
        }

        try
        {
            if (Roslyn is not null)
            {
                await Roslyn.DisposeAsync();
            }

            if (services is not null)
            {
                await services.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Sandbox end-to-end clean-up failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Containers, volumes, the network, the infrastructure and the image, by the fixture's labels.
        await Docker.DisposeAsync();
        DeleteTree(TempRoot);
    }

    /// <summary>Creates a run from the seed's main, waits until its sandbox is ready, and times both.</summary>
    public async Task<EndToEndRun> StartRunAsync(string name)
    {
        var runId = Guid.NewGuid();
        var watch = Stopwatch.StartNew();
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var created = await Provider.CreateAsync(new RunWorkspaceRequest(runId, "seed", Remote, "main", $"run/{name}", "Sandbox.sln"), bound.Token);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? $"{created.Error}\n{string.Join('\n', Log)}" : "");
        var createdIn = watch.Elapsed;

        var ready = await Provider.WaitAllReadyAsync(runId, TimeSpan.FromMinutes(10), CancellationToken.None);
        ready.IsSuccess.Should().BeTrue(ready.IsFailure ? $"{ready.Error}\n{string.Join('\n', Log)}" : "");
        Timings.Enqueue($"{name}: create {createdIn.TotalSeconds:F1} s, create to ready {watch.Elapsed.TotalSeconds:F1} s");
        return new EndToEndRun(this, runId);
    }

    /// <summary>The schemas of the <c>roslyn</c> route, listed once from the first sandbox that asks.</summary>
    /// <remarks>
    /// No host Roslyn server exists in this test, so the schemas come from a sandbox's own <c>/mcp/roslyn</c> listing;
    /// acceptable in a test, where the sandbox and the API run the same RoslynCodeLens version. Production takes them
    /// from a host-wide server.
    /// </remarks>
    /// <remarks>The tests of one class run one at a time, so the list is never fetched twice at once.</remarks>
    internal async Task<IReadOnlyList<AITool>> RoslynSchemasAsync(Guid runId, CancellationToken ct)
    {
        if (roslynSchemas is not null)
        {
            return roslynSchemas;
        }

        var endpoint = await Provider.ResolveAsync(runId, "roslyn", ct);
        endpoint.Should().NotBeNull("the run's sandbox is ready");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint!.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {endpoint.BearerToken}" },
            EnableStandaloneGetStream = false,
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        tools.Should().NotBeEmpty("a ready sandbox lists RoslynCodeLens's tools");
        roslynSchemas = [.. tools];
        return roslynSchemas;
    }

    /// <summary>The <c>workspace</c> or <c>sandbox</c> source the provider registered.</summary>
    internal IToolSource Source(string name) => string.Equals(name, "roslyn", StringComparison.Ordinal)
        ? Roslyn
        : services!.GetServices<IToolSource>().Single(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Every <paramref name="fileName"/> under <paramref name="root"/>, recursively; inaccessible directories are skipped.</summary>
    public static IReadOnlyList<string> FindFiles(string root, string fileName) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, fileName, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None })]
            : [];

    private ServiceProvider BuildServices()
    {
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new QueueLoggers(log)));
        collection.AddSingleton<ISandboxRuntime>(Runtime);
        collection.AddThalos(t => t.UseSandboxRunWorkspaces(o =>
        {
            o.DataRoot = DataRoot;
            o.Image = Image;
            foreach (var entry in ShippedProtectedPaths)
            {
                o.ProtectedPaths.Add(entry);
            }
        }));
        return collection.BuildServiceProvider();
    }

    /// <summary>
    /// Builds <c>Image/Dockerfile</c> with the repository root as its context, sending only what the build stage copies,
    /// without any <c>bin</c> or <c>obj</c>. Bounded at 25 minutes; a failed build throws with the build's last output.
    /// </summary>
    private async Task BuildImageAsync()
    {
        var root = RepositoryRoot();
        using var context = new MemoryStream();
        using (var writer = new TarWriter(context, TarEntryFormat.Pax, leaveOpen: true))
        {
            await writer.WriteEntryAsync(Path.Combine(root, "tests", "Thalos.NET.Tests.Sandbox.Docker", "Image", "Dockerfile"), "Dockerfile");
            foreach (var file in (string[])["global.json", "Directory.Build.props", "Directory.Packages.props"])
            {
                await writer.WriteEntryAsync(Path.Combine(root, file), file);
            }

            foreach (var tree in (string[])["src", Path.Combine("samples", "Thalos.Sample.SandboxHost")])
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, tree), "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!IsBuildOutput(relative))
                    {
                        await writer.WriteEntryAsync(file, relative);
                    }
                }
            }
        }

        context.Position = 0;
        var output = new ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        using var client = new DockerClientBuilder().WithTimeout(TimeSpan.FromMinutes(25)).Build();
        await client.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters
            {
                Tags = [Image],
                Labels = new Dictionary<string, string>(StringComparer.Ordinal) { [DockerSandboxFixture.TestLabel] = Docker.Suffix },
                BuildArgs = new Dictionary<string, string>(StringComparer.Ordinal) { ["TEST_LABEL"] = Docker.Suffix },
                Remove = true,
                ForceRemove = true,
            },
            context,
            authConfigs: null,
            headers: null,
            new Progress<JSONMessage>(m => output.Enqueue(m.Error?.Message ?? m.Stream ?? m.Status ?? "")),
            timeout.Token);

        var built = await client.Images.ListImagesAsync(new ImagesListParameters { Filters = DockerSandboxFixture.Filter("reference", Image) }, timeout.Token);
        built.Should().NotBeEmpty($"the image builds:\n{string.Concat(output.TakeLast(60))}");
    }

    private static bool IsBuildOutput(string relative)
    {
        foreach (var segment in relative.Split('/'))
        {
            if (segment is "bin" or "obj")
            {
                return true;
            }
        }

        return false;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Thalos.NET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root, holding Thalos.NET.slnx, was not found above the test binaries.");
    }

    /// <summary>
    /// The seed: <c>Sandbox.sln</c>, <c>Lib</c> referencing Newtonsoft.Json with one deliberate warning, and
    /// <c>Lib.Tests</c> with one xunit test, committed on <c>main</c> of a bare repository.
    /// </summary>
    private async Task SeedRepositoryAsync()
    {
        var seed = Path.Combine(TempRoot, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "Lib"));
        Directory.CreateDirectory(Path.Combine(seed, "Lib.Tests"));
        foreach (var (name, content) in SeedFiles)
        {
            File.WriteAllText(Path.Combine(seed, name), content.ReplaceLineEndings("\n"));
        }

        await GitAsync(TempRoot, "init", "--bare", "--initial-branch=main", Remote);
        await GitAsync(seed, "init", "--initial-branch=main");
        await GitAsync(seed, "-c", "core.autocrlf=false", "add", "-A");
        await GitAsync(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "seed");
        await GitAsync(seed, "push", Remote, "main");
    }

    private static readonly (string Name, string Content)[] SeedFiles =
    [
        ("Sandbox.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{6F1A2B3C-0000-4000-8000-000000000001}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib.Tests", "Lib.Tests\Lib.Tests.csproj", "{6F1A2B3C-0000-4000-8000-000000000002}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{6F1A2B3C-0000-4000-8000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{6F1A2B3C-0000-4000-8000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{6F1A2B3C-0000-4000-8000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{6F1A2B3C-0000-4000-8000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU
            	EndGlobalSection
            EndGlobal

            """),
        (".gitignore", "bin/\nobj/\n"),
        ("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>

            """),
        ("Lib/A.cs", """
            namespace Lib;

            public static class A
            {
                public static string Json(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);

                // CS0219, on purpose: proves the diagnostics the tests read are really computed.
                public static int Unused()
                {
                    var unused = 1;
                    return 0;
                }
            }

            """),
        ("Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>

            """),
        ("Lib.Tests/ATests.cs", """
            namespace Lib.Tests;

            public class ATests
            {
                [Xunit.Fact]
                public void Json_serialises() => Xunit.Assert.Equal("{\"X\":1}", Lib.A.Json(new { X = 1 }));
            }

            """),
    ];

    private static async Task GitAsync(string directory, params string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await stdout;
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', args)}: {await stderr}");
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temp directory left behind is harmless.
        }
    }

    /// <summary>Lists the <c>roslyn</c> schemas from the first sandbox asked, through <see cref="RoslynSchemasAsync"/>.</summary>
    private sealed class SandboxListedSchemas(SandboxEndToEndFixture fixture) : IToolSource
    {
        public string Name => "roslyn";

        public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct) =>
            ValueTask.FromResult(fixture.roslynSchemas is { } listed
                ? Result<IReadOnlyList<AITool>, AgentError>.Success(listed)
                : Result<IReadOnlyList<AITool>, AgentError>.Failure(AgentError.Validation("No sandbox has listed the roslyn schemas yet.")));
    }

    private sealed class QueueLoggers(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {category} {logLevel}: {formatter(state, exception)} {exception?.Message}");
        }
    }
}

/// <summary>One run with a ready sandbox. Calls its tools as the run's caller; disposing removes the run, bounded.</summary>
public sealed class EndToEndRun(SandboxEndToEndFixture fixture, Guid runId) : IAsyncDisposable
{
    public Guid RunId { get; } = runId;

    public string SandboxId => RunId.ToString("N");

    /// <summary>Calls <c>{source}__{tool}</c> as the run, bounded at 20 minutes, and returns the text result.</summary>
    public async Task<string> CallAsync(string source, string tool, params (string Name, object? Value)[] arguments)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        if (string.Equals(source, "roslyn", StringComparison.Ordinal))
        {
            await fixture.RoslynSchemasAsync(RunId, bound.Token);
        }

        var tools = await fixture.Source(source).GetToolsAsync(bound.Token);
        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        var function = tools.Value.OfType<AIFunction>().SingleOrDefault(f => string.Equals(f.Name, tool, StringComparison.Ordinal));
        function.Should().NotBeNull($"'{source}' offers '{tool}' (it offers {string.Join(", ", tools.Value.Select(t => t.Name))})");

        var args = new AIFunctionArguments(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        ISecurityContext caller = new RunCaller(RunId);
        using var turn = TurnScope.Begin(SessionId.New(), TurnId.New(), caller);
        return (await function!.InvokeAsync(args, bound.Token))?.ToString() ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await fixture.Provider.RemoveAsync(RunId, bound.Token);
        }
        catch (Exception ex)
        {
            // Best effort: the fixture removes every container and volume by label afterwards anyway.
            await Console.Error.WriteLineAsync($"Removing run {RunId} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class RunCaller(Guid runId) : ISecurityContext
    {
        public string Id { get; } = $"run:{runId:D}";

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Claims { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = runId.ToString("D") };
    }
}
