using NSubstitute;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>Tests for <see cref="ProcessValidator"/>'s shape rules and resolver-backed reference checks.</summary>
public sealed class ProcessValidatorTests
{
    // yaml fragment for `nodes:`, expected message fragment, should it be accepted
    //
    // Every rejection here is paired with an accepted case that differs by the smallest edit that flips the
    // verdict, so a rule can't pass "rejects malformed input" by rejecting everything:
    //   - unknown 'next' target:        row 2 rejects, row 1 (b vs c) accepts
    //   - branch key not an outcome:    row 4 rejects, row 3 (nope vs ok) accepts
    //   - unreachable node:             row 5 rejects, row 1 (drop node 'c') accepts
    //   - no path to a terminal:        row 6 rejects, row 1 (drop the a<-b loop-back) accepts
    //   - unknown 'onExceeded' target:  row 8 rejects, row 7 (zz vs b) accepts
    //   - branch without outcomes:      row 9 rejects, row 3 (add 'outcomes: [ok], ') accepts
    //   - not exactly one node kind:    row 10 rejects, row 1 (drop 'terminal: succeeded, ') accepts
    //   - agent without skill:          row 11 rejects, row 1 (drop 'skill: s, ') accepts
    //   - skill without agent:          row 12 rejects, row 1 (drop 'agent: x, ') accepts
    //   - maxVisits without onExceeded: row 14 rejects, row 13 (add ', onExceeded: b') accepts
    //   - onExceeded without maxVisits: row 15 rejects, row 13 (drop ', maxVisits: 3') accepts
    //   - unrecognized terminal status: row 16 rejects, row 1 (succeeded vs banana) accepts
    public static TheoryData<string, string, bool> Cases => new()
    {
        { "a: { agent: x, skill: s, next: b }\n  b: { terminal: succeeded }", "", true },
        { "a: { agent: x, skill: s, next: c }\n  b: { terminal: succeeded }", "unknown node 'c'", false },
        { "a: { agent: x, skill: s, outcomes: [ok], branch: { ok: b } }\n  b: { terminal: succeeded }", "", true },
        { "a: { agent: x, skill: s, outcomes: [ok], branch: { nope: b } }\n  b: { terminal: succeeded }", "branch key 'nope' is not a declared outcome", false },
        { "a: { agent: x, skill: s, next: b }\n  b: { terminal: succeeded }\n  c: { terminal: succeeded }", "unreachable node 'c'", false },
        { "a: { agent: x, skill: s, next: b }\n  b: { agent: x, skill: s, next: a }", "no path from 'a' reaches a terminal", false },
        { "a: { agent: x, skill: s, outcomes: [ok], branch: { ok: a }, maxVisits: 3, onExceeded: b }\n  b: { terminal: succeeded }", "", true },
        { "a: { agent: x, skill: s, outcomes: [ok], branch: { ok: a }, maxVisits: 3, onExceeded: zz }\n  b: { terminal: succeeded }", "unknown node 'zz'", false },
        { "a: { agent: x, skill: s, branch: { ok: b } }\n  b: { terminal: succeeded }", "declares 'branch' without declaring 'outcomes'", false },
        { "a: { agent: x, skill: s, terminal: succeeded, next: b }\n  b: { terminal: succeeded }", "must be exactly one of task, gate or terminal", false },
        { "a: { agent: x, next: b }\n  b: { terminal: succeeded }", "has 'agent' but no 'skill'", false },
        { "a: { skill: s, next: b }\n  b: { terminal: succeeded }", "has 'skill' but no 'agent'", false },
        { "a: { agent: x, skill: s, next: b, maxVisits: 3, onExceeded: b }\n  b: { terminal: succeeded }", "", true },
        { "a: { agent: x, skill: s, next: b, maxVisits: 3 }\n  b: { terminal: succeeded }", "declares 'maxVisits' but no 'onExceeded' target", false },
        { "a: { agent: x, skill: s, next: b, onExceeded: b }\n  b: { terminal: succeeded }", "declares 'onExceeded' but no 'maxVisits' cap", false },
        { "a: { agent: x, skill: s, next: b }\n  b: { terminal: banana }", "has an unrecognized terminal status 'banana'", false },
    };

    [Theory, MemberData(nameof(Cases))]
    public async Task Validate_accepts_valid_and_names_the_specific_fault(string nodes, string expected, bool ok)
    {
        var def = ProcessLoader.Load($"process: p\nversion: 1\nnodes:\n  {nodes}").Value;

        var result = await ProcessValidator.ValidateAsync(def, resolver: null, CancellationToken.None);

        result.IsSuccess.Should().Be(ok);
        if (!ok)
        {
            result.Error.Should().Contain(expected);
        }
    }

    [Fact]
    public async Task ValidateAsync_names_the_node_and_the_missing_agent_when_the_resolver_rejects_it()
    {
        var yaml = """
            process: p
            version: 1
            nodes:
              a: { agent: ghost-writer, skill: draft, next: b }
              b: { terminal: succeeded }
            """;
        var def = ProcessLoader.Load(yaml).Value;
        var resolver = Substitute.For<IWorkflowReferenceResolver>();
        resolver.AgentExistsAsync("ghost-writer", Arg.Any<CancellationToken>()).Returns(false);
        resolver.SkillExistsAsync("draft", Arg.Any<CancellationToken>()).Returns(true);

        var result = await ProcessValidator.ValidateAsync(def, resolver, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("node 'a'").And.Contain("ghost-writer");
    }

    [Fact]
    public async Task ValidateAsync_accepts_when_the_resolver_confirms_every_agent_and_skill()
    {
        var yaml = """
            process: p
            version: 1
            nodes:
              a: { agent: ghost-writer, skill: draft, next: b }
              b: { terminal: succeeded }
            """;
        var def = ProcessLoader.Load(yaml).Value;
        var resolver = Substitute.For<IWorkflowReferenceResolver>();
        resolver.AgentExistsAsync("ghost-writer", Arg.Any<CancellationToken>()).Returns(true);
        resolver.SkillExistsAsync("draft", Arg.Any<CancellationToken>()).Returns(true);

        var result = await ProcessValidator.ValidateAsync(def, resolver, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
    }
}
