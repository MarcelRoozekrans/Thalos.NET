using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// A canonical dump, one line per node, of the <see cref="ProcessDefinition"/> <see cref="ProcessLoader"/> builds, captured before the
/// loader moves to a YAML static context: Daedalus' two process files, copied verbatim into <c>Fixtures/processes</c>,
/// <see cref="ProcessLoaderTests"/>' inline fixtures, and one fixture that sets every node field. The dump is written
/// by the reflection-based serializer in test code, which stays allowed as an oracle. Characterization tests: they
/// pass on today's code.
/// </summary>
public sealed class ProcessLoaderGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><see cref="ProcessLoaderTests.Load_parses_sequence_branch_loopback_and_gate"/>'s fixture.</summary>
    private const string SequenceBranchLoopbackGate = """
        process: manufacture
        version: 3
        nodes:
          implement: { agent: backend, skill: tdd, next: review }
          review:
            agent: reviewer
            skill: code-review
            outcomes: [approved, rejected]
            branch: { approved: gate, rejected: implement }
            maxVisits: 5
            onExceeded: adjudicate
          gate: { await: human_approval, next: publish }
          publish: { agent: publisher, skill: finish, next: done }
          adjudicate: { agent: lead, skill: adjudicate, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary><see cref="ProcessLoaderTests.An_action_node_parses_with_outcomes_and_branch"/>'s fixture.</summary>
    private const string ActionNode = """
        process: p
        version: 1
        nodes:
          publish:
            action: open-pull-request
            outcomes: [published, failed]
            branch: { published: done, failed: stop }
          done: { terminal: succeeded }
          stop: { terminal: failed }
        """;

    /// <summary><see cref="ProcessLoaderTests.A_task_node_has_no_action"/>'s fixture.</summary>
    private const string TaskNode = """
        process: p
        version: 1
        nodes:
          work: { agent: backend, skill: tdd, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>Every node field once, a branch whose keys are not in sorted order, and a node with none of the optional ones.</summary>
    private const string EveryField = """
        process: every-field
        version: 7
        nodes:
          review:
            agent: reviewer
            skill: code-review
            models: [model-a, model-b]
            lenses: [security, tests]
            quorum: 2
            outcomes: [rejected, approved, blocked]
            branch: { rejected: fix, approved: done, blocked: stop }
            maxVisits: 3
            onExceeded: stop
            unknownKey: ignored
          fix: { agent: implementer, skill: fix, next: review }
          done: { terminal: succeeded }
          stop: { terminal: failed }
        """;

    // Red: in ProcessLoader, use UnderscoredNamingConvention instead of CamelCaseNamingConvention; maxVisits and
    // onExceeded then no longer bind and the dump differs.
    [Theory]
    [InlineData("manufacture.yaml")]
    [InlineData("review-replay.yaml")]
    public void A_Daedalus_process_file_loads_as_before(string file)
    {
        var dump = Dump(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "processes", file)));

        dump.Should().Be(file switch
        {
            "manufacture.yaml" => """
                manufacture v9, start implement
                implement: {"Agent":"implementer","Skill":"manufacture-implement","Action":null,"Next":null,"Branch":{"changed":"review","blocked":"adjudicate"},"Outcomes":["changed","blocked"],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                review: {"Agent":"reviewer","Skill":"manufacture-review","Action":null,"Next":null,"Branch":{"approved":"retrospect","rejected":"implement"},"Outcomes":["approved","rejected"],"Await":null,"Terminal":null,"MaxVisits":5,"OnExceeded":"adjudicate","Models":[],"Lenses":["correctness","falsifiability","mechanism"],"Quorum":null}
                retrospect: {"Agent":"reviewer","Skill":"manufacture-retrospect","Action":null,"Next":null,"Branch":{"proposed":"gate","none":"gate"},"Outcomes":["proposed","none"],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                adjudicate: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"failed","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                gate: {"Agent":null,"Skill":null,"Action":null,"Next":"publish","Branch":{},"Outcomes":[],"Await":"human_approval","Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                publish: {"Agent":null,"Skill":null,"Action":"open-pull-request","Next":null,"Branch":{"published":"file-findings","failed":"adjudicate"},"Outcomes":["published","failed"],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                file-findings: {"Agent":null,"Skill":null,"Action":"file-review-findings","Next":null,"Branch":{"filed":"done","none":"done"},"Outcomes":["filed","none"],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                done: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"succeeded","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                """,
            _ => """
                review-replay v1, start review
                review: {"Agent":"reviewer","Skill":"manufacture-review","Action":null,"Next":null,"Branch":{"approved":"done","rejected":"done"},"Outcomes":["approved","rejected"],"Await":null,"Terminal":null,"MaxVisits":1,"OnExceeded":"done","Models":[],"Lenses":["correctness","falsifiability","mechanism"],"Quorum":null}
                done: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"succeeded","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
                """,
        });
    }

    // Red: in ProcessLoader, use UnderscoredNamingConvention instead of CamelCaseNamingConvention; maxVisits and
    // onExceeded then no longer bind and the dump differs.
    [Fact]
    public void The_sequence_branch_loopback_and_gate_fixture_loads_as_before() =>
        Dump(SequenceBranchLoopbackGate).Should().Be("""
            manufacture v3, start implement
            implement: {"Agent":"backend","Skill":"tdd","Action":null,"Next":"review","Branch":{},"Outcomes":[],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            review: {"Agent":"reviewer","Skill":"code-review","Action":null,"Next":null,"Branch":{"approved":"gate","rejected":"implement"},"Outcomes":["approved","rejected"],"Await":null,"Terminal":null,"MaxVisits":5,"OnExceeded":"adjudicate","Models":[],"Lenses":[],"Quorum":null}
            gate: {"Agent":null,"Skill":null,"Action":null,"Next":"publish","Branch":{},"Outcomes":[],"Await":"human_approval","Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            publish: {"Agent":"publisher","Skill":"finish","Action":null,"Next":"done","Branch":{},"Outcomes":[],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            adjudicate: {"Agent":"lead","Skill":"adjudicate","Action":null,"Next":"done","Branch":{},"Outcomes":[],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            done: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"succeeded","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            """);

    // Red: in ProcessNodeDto.ToProcessNode, map Action to null; the action node's dump loses it.
    [Fact]
    public void The_action_node_fixture_loads_as_before() =>
        Dump(ActionNode).Should().Be("""
            p v1, start publish
            publish: {"Agent":null,"Skill":null,"Action":"open-pull-request","Next":null,"Branch":{"published":"done","failed":"stop"},"Outcomes":["published","failed"],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            done: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"succeeded","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            stop: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"failed","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            """);

    // Red: in ProcessNodeDto.ToProcessNode, map Next to null; the task node's dump loses it.
    [Fact]
    public void The_task_node_fixture_loads_as_before() =>
        Dump(TaskNode).Should().Be("""
            p v1, start work
            work: {"Agent":"backend","Skill":"tdd","Action":null,"Next":"done","Branch":{},"Outcomes":[],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            done: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"succeeded","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            """);

    // Red: in ProcessLoader, drop IgnoreUnmatchedProperties; unknownKey then fails the load. Red: in
    // ProcessNodeDto.ToProcessNode, map Quorum to null; the review node's dump loses it.
    [Fact]
    public void The_every_field_fixture_loads_as_before() =>
        Dump(EveryField).Should().Be("""
            every-field v7, start review
            review: {"Agent":"reviewer","Skill":"code-review","Action":null,"Next":null,"Branch":{"rejected":"fix","approved":"done","blocked":"stop"},"Outcomes":["rejected","approved","blocked"],"Await":null,"Terminal":null,"MaxVisits":3,"OnExceeded":"stop","Models":["model-a","model-b"],"Lenses":["security","tests"],"Quorum":2}
            fix: {"Agent":"implementer","Skill":"fix","Action":null,"Next":"review","Branch":{},"Outcomes":[],"Await":null,"Terminal":null,"MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            done: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"succeeded","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            stop: {"Agent":null,"Skill":null,"Action":null,"Next":null,"Branch":{},"Outcomes":[],"Await":null,"Terminal":"failed","MaxVisits":null,"OnExceeded":null,"Models":[],"Lenses":[],"Quorum":null}
            """);

    private static string Dump(string yaml)
    {
        var loaded = ProcessLoader.Load(yaml);
        loaded.IsSuccess.Should().BeTrue(loaded.IsFailure ? loaded.Error : "");
        var definition = loaded.Value;
        var dump = new StringBuilder($"{definition.Name} v{definition.Version}, start {definition.StartNode}");
        foreach (var (name, node) in definition.Nodes)
        {
            dump.Append('\n').Append(name).Append(": ").Append(JsonSerializer.Serialize(node, DumpOptions));
        }

        return dump.ToString();
    }
}
