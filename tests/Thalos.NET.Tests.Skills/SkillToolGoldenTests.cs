using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Thalos.Skills;
using Thalos.Tools;

namespace Thalos.Tests.Skills;

/// <summary>
/// The skill tools through <see cref="SkillToolSource"/>, whose tools come from the reflective
/// <see cref="Thalos.Tools.LocalToolSource"/> today, captured before they move to typed binders: the
/// <see cref="ToolOracle"/> dump, and the result text of a call to every tool, over a skill body the encoder escapes.
/// Characterization tests: they pass on today's code.
/// </summary>
public sealed class SkillToolGoldenTests
{
    // Red: in LocalToolSourceBinding.Options, set Description = name; every description in the dump changes.
    [Fact]
    public async Task The_skill_tools_dump_as_before()
    {
        var (services, _, _) = SkillToolsTests.Host(["*"]);
        var dump = ToolOracle.Dump((await new SkillToolSource(services, Options.Create(new SkillOptions())).GetToolsAsync(CancellationToken.None)).Value);

        dump.Should().Be("""
            load | Load the full text of a skill (a procedure document) by name. Names come from the <skills> block in your instructions or from skills__search. | {"type":"object","properties":{"name":{"description":"The skill name, e.g. dotnet-migrations.","type":"string"}},"required":["name"]} | {"type":"string"}
            search | Search the skills available to this agent by what they do. Returns matching names with their descriptions; use skills__load to read one. | {"type":"object","properties":{"query":{"description":"What you need to do, in your own words.","type":"string"},"topK":{"description":"Max results, 1..20 (default 5).","type":["integer","null"],"default":null}},"required":["query"]} | {"type":"string"}
            """);
    }

    // Red: in LocalToolSourceBinding.Options, default SerializerOptions to a copy of AIJsonUtilities.DefaultOptions
    // with another Encoder; the escaping of the skill text in the results changes.
    [Fact]
    public async Task Every_skill_tool_answers_as_before()
    {
        var text = await AnswersAsync(static services => new SkillToolSource(services, Options.Create(new SkillOptions())));

        text.Should().Be("""
            load: "<skill name=\"release\">\n# Releasing\n1. Tag it <now> & push 'é' \uD83D\uDE80.\n</skill>"
            search: "Skill search is unavailable; the <skills> block in your instructions lists every skill you can load."
            """);
    }

    [Fact]
    public async Task Skills_typed_tools_match_the_reflective_oracle()
    {
        var (services, _, _) = SkillToolsTests.Host(["*"]);
        var typedDump = ToolOracle.Dump((await new SkillToolSource(services, Options.Create(new SkillOptions())).GetToolsAsync(CancellationToken.None)).Value);
        var reflectiveDump = ToolOracle.Dump((await Reflective(services).GetToolsAsync(CancellationToken.None)).Value);
        var typed = await AnswersAsync(static services => new SkillToolSource(services, Options.Create(new SkillOptions())));
        var reflective = await AnswersAsync(Reflective);

        // Every failing assertion is reported, so each Red below shows on its own line.
        using var scope = new AssertionScope();

        // Red: bind SkillToolSource's tools in another order, search before load; the dump lines change places.
        typedDump.Should().Be(reflectiveDump);

        // Red: build SkillToolSource's options from SkillToolsJsonContext.Default.Options instead of
        // LocalToolSources.CreateOptions; its encoder escapes the skill text, so the results differ.
        typed.Should().Be(reflective);

        // Red: store a plain body instead of the tricky one; the results no longer carry the characters the encoder decides on.
        reflective.Should().Contain("<now> & push 'é' \\uD83D\\uDE80");
    }

    private static LocalToolSource Reflective(IServiceProvider services) => new(SkillToolSource.SourceName, services, [typeof(SkillTools)]);

    /// <summary>A call to every tool of the source <paramref name="create"/> builds, over a skill body the encoder escapes.</summary>
    private static async Task<string> AnswersAsync(Func<IServiceProvider, IToolSource> create)
    {
        var (services, agent, store) = SkillToolsTests.Host(["*"]);
        await store.UpsertAsync(SkillModelTests.Doc("release", description: "Cut a release <a & 'é' 🚀>.", body: "# Releasing\n1. Tag it <now> & push 'é' 🚀."), CancellationToken.None);
        var tools = (await create(services).GetToolsAsync(CancellationToken.None)).Value.Cast<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        using var turn = SkillToolsTests.Turn(agent);

        var load = ToolOracle.Result(await tools["load"].InvokeAsync(SkillToolsTests.Args(("name", "release"))));
        var search = ToolOracle.Result(await tools["search"].InvokeAsync(SkillToolsTests.Args(("query", "release <é>"))));
        return $"load: {load}\nsearch: {search}";
    }
}
