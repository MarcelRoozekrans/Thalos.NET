using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>Tests for <see cref="ProcessLoader"/> over the four graph primitives: sequence, branch, loop-back, and approval gate.</summary>
public sealed class ProcessLoaderTests
{
    [Fact]
    public void Load_parses_sequence_branch_loopback_and_gate()
    {
        var yaml = """
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

        var result = ProcessLoader.Load(yaml);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        var p = result.Value;
        p.Name.Should().Be("manufacture");
        p.Version.Should().Be(3);
        p.StartNode.Should().Be("implement");
        p.Nodes["review"].Branch["rejected"].Should().Be("implement");   // loop-back
        p.Nodes["review"].MaxVisits.Should().Be(5);
        p.Nodes["review"].OnExceeded.Should().Be("adjudicate");
        p.Nodes["gate"].Await.Should().Be("human_approval");
        p.Nodes["done"].Terminal.Should().Be("succeeded");
    }

    [Fact]
    public void An_action_node_parses_with_outcomes_and_branch()
    {
        var p = ProcessLoader.Load("""
            process: p
            version: 1
            nodes:
              publish:
                action: open-pull-request
                outcomes: [published, failed]
                branch: { published: done, failed: stop }
              done: { terminal: succeeded }
              stop: { terminal: failed }
            """).Value;

        p.Nodes["publish"].Action.Should().Be("open-pull-request");
        p.Nodes["publish"].Agent.Should().BeNull();
    }

    [Fact]
    public void A_task_node_has_no_action()
    {
        var p = ProcessLoader.Load("""
            process: p
            version: 1
            nodes:
              work: { agent: backend, skill: tdd, next: done }
              done: { terminal: succeeded }
            """).Value;

        p.Nodes["work"].Action.Should().BeNull();
    }
}
