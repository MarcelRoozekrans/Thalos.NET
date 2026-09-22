using Thalos;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Records the last <see cref="SubagentRunRequest"/> <see cref="WorkflowNodeDispatcher"/> built and returns
/// whatever <see cref="NextResult"/> is configured to produce for it. Stands in for a real
/// <see cref="ISubagentRunner"/> — dispatching an actual subagent is out of scope for these tests.
/// </summary>
internal sealed class FakeSubagentRunner : ISubagentRunner
{
    /// <summary>The most recent request passed to <see cref="RunAsync"/>, or <see langword="null"/> before the first call.</summary>
    public SubagentRunRequest? LastRequest { get; private set; }

    /// <summary>How many times <see cref="RunAsync"/> has been called.</summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Produces the result for the next (and every subsequent) call, given the request that was passed. Must be
    /// set before <see cref="RunAsync"/> is called; a test that calls without configuring this gets an
    /// <see cref="InvalidOperationException"/> rather than a silently misleading default result.
    /// </summary>
    public Func<SubagentRunRequest, Result<AgentTurnResult, AgentError>>? NextResult { get; set; }

    public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        CallCount++;

        if (NextResult is null)
        {
            throw new InvalidOperationException($"{nameof(FakeSubagentRunner)}.{nameof(NextResult)} was not configured before {nameof(RunAsync)} was called.");
        }

        return ValueTask.FromResult(NextResult(request));
    }
}
