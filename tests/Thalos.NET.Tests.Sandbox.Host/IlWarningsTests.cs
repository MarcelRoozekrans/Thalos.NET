namespace Thalos.Tests.Sandbox.Host;

/// <summary>
/// <see cref="IlWarnings"/> over the lines of Daedalus's <c>eng/aot/tests/sample-ilc.log</c>, with <c>@REPO@</c> as
/// <c>/repo</c>: the analyzer's located warning, ILC's located warning of the same site, ILC's unlocated warnings of
/// package code, a non-IL warning, and MSBuild's end-of-build repeat. The keys are the ones that script's own test
/// expects.
/// </summary>
public sealed class IlWarningsTests
{
    private const string Repo = "/repo";

    private const string Analyzer = "/repo/src/Daedalus.Sandbox/Program.cs(5,27): warning IL3050: Using member 'Thalos.Sandbox.Host.SandboxHost.CreateBuilder(String[])' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. Tool parameters and results are serialized via reflection-based JSON. [/repo/src/Daedalus.Sandbox/Daedalus.Sandbox.csproj]";

    private const string IlcLocated = "/repo/src/Daedalus.Sandbox/Program.cs(5): AOT analysis warning IL3050: Program.<<Main>$>d__0.MoveNext(): Using member 'Thalos.Sandbox.Host.SandboxHost.CreateBuilder(String[])' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. Tool parameters and results are serialized via reflection-based JSON. [/repo/src/Daedalus.Sandbox/Daedalus.Sandbox.csproj]";

    private const string IlcMapGet = "ILC : Trim analysis warning IL2026: Thalos.Sandbox.Host.SandboxHost.Map(WebApplication): Using member 'Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(IEndpointRouteBuilder,String,Delegate)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced. [/repo/src/Daedalus.Sandbox/Daedalus.Sandbox.csproj]";

    private const string IlcMapPost = "ILC : Trim analysis warning IL2026: Thalos.Sandbox.Host.SandboxHost.Map(WebApplication): Using member 'Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapPost(IEndpointRouteBuilder,String,Delegate)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced. [/repo/src/Daedalus.Sandbox/Daedalus.Sandbox.csproj]";

    private const string Obsolete = "/repo/src/Daedalus.Sandbox/Program.cs(9,13): warning CS0618: 'Foo.Bar()' is obsolete: 'Use Baz'. [/repo/src/Daedalus.Sandbox/Daedalus.Sandbox.csproj]";

    private const string MapGetKey = "IL2026|||Thalos.Sandbox.Host.SandboxHost.Map(WebApplication): Using member 'Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(IEndpointRouteBuilder,String,Delegate)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced.";

    private const string MapPostKey = "IL2026|||Thalos.Sandbox.Host.SandboxHost.Map(WebApplication): Using member 'Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapPost(IEndpointRouteBuilder,String,Delegate)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced.";

    private const string CreateBuilderKey = "IL3050|src/Daedalus.Sandbox/Program.cs|5|Using member 'Thalos.Sandbox.Host.SandboxHost.CreateBuilder(String[])' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. Tool parameters and results are serialized via reflection-based JSON.";

    private static string Log(params string[] lines) => string.Join("\r\n", lines);

    [Fact]
    public void An_unlocated_ILC_warning_is_counted_and_keyed_by_its_origin_and_member()
    {
        // Red: anchor the warning pattern at the line start with no colon before it, ^[^:]*warning, as the first
        // version did: "ILC : Trim analysis warning" is not matched, and nothing is counted.
        IlWarnings.Distinct(Log(IlcMapGet, IlcMapPost), Repo).Should().Equal(MapGetKey, MapPostKey);
    }

    [Fact]
    public void ILC_and_the_analyzer_reporting_one_site_are_one_warning()
    {
        // Red: keep ILC's origin in a located warning's member: the two reports become two keys.
        IlWarnings.Distinct(Log(Analyzer, IlcLocated), Repo).Should().Equal(CreateBuilderKey);
    }

    [Fact]
    public void A_located_warning_drops_the_column_and_the_repository_root()
    {
        // Red: keep the column in the location: the analyzer's key reads line "5,27".
        IlWarnings.Distinct(Analyzer, Repo).Should().Equal(CreateBuilderKey);
    }

    [Fact]
    public void The_whole_sample_counts_three_and_ignores_other_warnings_and_the_repeat()
    {
        var log = Log(
            "  Determining projects to restore...",
            Obsolete,
            Analyzer,
            "  Generating native code",
            IlcLocated,
            IlcMapGet,
            IlcMapPost,
            "Build succeeded.",
            Obsolete,
            IlcMapGet,
            "    2 Warning(s)");

        // Red: drop the removal of MSBuild's " [project]" suffix: every key ends in the project path.
        IlWarnings.Distinct(log, Repo).Should().Equal(MapGetKey, MapPostKey, CreateBuilderKey);
    }
}
