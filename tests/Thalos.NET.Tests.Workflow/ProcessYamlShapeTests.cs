using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// The YAML shapes <see cref="ProcessLoader"/>'s typed pass has to bind: an <c>int?</c> that is absent stays null, one that
/// is present binds, and a <c>branch</c> map keeps the order its keys were written in.
/// </summary>
public sealed class ProcessYamlShapeTests
{
    // Characterization test: passes on the reflection deserializer too.
    // Red: make ProcessNodeDto.MaxVisits and ProcessNodeDto.Quorum `int` with default 0; both then load as 0.
    [Fact]
    public void An_absent_max_visits_and_quorum_stay_null()
    {
        var node = LoadNode("""
            process: p
            version: 1
            nodes:
              work: { agent: backend, skill: tdd, next: done }
              done: { terminal: succeeded }
            """, "work");

        node.MaxVisits.Should().BeNull();
        node.Quorum.Should().BeNull();
    }

    // Red: rename ProcessNodeDto.MaxVisits to MaxVisitCount, so that `maxVisits` no longer maps.
    // Red: rename ProcessNodeDto.Quorum to QuorumSize, so that `quorum` no longer maps.
    [Fact]
    public void A_present_max_visits_and_quorum_bind()
    {
        var node = LoadNode("""
            process: p
            version: 1
            nodes:
              review: { agent: reviewer, skill: code-review, next: done, maxVisits: 3, quorum: 2 }
              done: { terminal: succeeded }
            """, "review");

        node.MaxVisits.Should().Be(3);
        node.Quorum.Should().Be(2);
    }

    // Characterization test: passes on the reflection deserializer too.
    // Red: in ProcessNodeDto.ToProcessNode, sort the branch keys ordinally; the order becomes a, b, c.
    [Fact]
    public void A_branch_map_keeps_its_key_order()
    {
        var node = LoadNode("""
            process: p
            version: 1
            nodes:
              decide: { agent: lead, skill: decide, outcomes: [b, a, c], branch: { b: x, a: y, c: z } }
              x: { terminal: succeeded }
              y: { terminal: succeeded }
              z: { terminal: failed }
            """, "decide");

        node.Branch.Keys.Should().Equal("b", "a", "c");
        node.Branch.Values.Should().Equal("x", "y", "z");
    }

    private static ProcessNode LoadNode(string yaml, string name)
    {
        var loaded = ProcessLoader.Load(yaml);
        loaded.IsSuccess.Should().BeTrue(loaded.IsFailure ? loaded.Error : "");
        return loaded.Value.Nodes[name];
    }
}
