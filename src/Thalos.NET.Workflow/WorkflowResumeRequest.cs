namespace Thalos.Workflow;

/// <summary>
/// Everything <see cref="IWorkflowStore.ResumeAsync(Guid,WorkflowResumeRequest,CancellationToken)"/> needs to
/// resume a run parked at a gate, including the approver a human gate exists to record.
/// </summary>
public sealed record WorkflowResumeRequest
{
    /// <summary>The signal the run's gate is awaiting — must match <see cref="WorkflowRun.AwaitingSignal"/>.</summary>
    public required string Signal { get; init; }

    /// <summary>
    /// Carried into the run's variables under the literal key <c>"payload"</c>, or <see langword="null"/> for a
    /// resume with nothing to attach.
    /// </summary>
    public string? Payload { get; init; }

    /// <summary>
    /// Who resumed the run. Required and non-null: every resume through a gate has an approver, so an
    /// implementation guards it with <see cref="ArgumentNullException.ThrowIfNull(object?,string?)"/> next to its
    /// existing <see cref="ArgumentException"/> guards, since <c>required</c> alone does not stop a caller passing
    /// <see langword="null"/> at the language boundary. A missing approver is a programming error, not an
    /// expected, caller-triggerable failure, so it throws rather than returning a <see cref="ZeroAlloc.Results.Result"/>
    /// failure.
    /// </summary>
    public required RunPrincipal ResumedBy { get; init; }
}
