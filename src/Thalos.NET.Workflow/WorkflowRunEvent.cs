namespace Thalos.Workflow;

/// <summary>
/// One entry of a run's append-only event log, as <see cref="IWorkflowRunHistory.ListEventsAsync"/> reads it back.
/// </summary>
/// <param name="Seq">
/// The seq the event was recorded at. The seeded <see cref="WorkflowEventKind.Entered"/> event is at 0; a
/// completion is at the seq of the node execution it closes.
/// </param>
/// <param name="Kind">
/// The event kind: a <see cref="WorkflowEventKind"/> member's name, or <c>Cancelled</c>, which
/// <see cref="IWorkflowStore.CancelAsync"/> records and <see cref="WorkflowEventKind"/> has no member for.
/// </param>
/// <param name="FromNode">The node the run left, or <see langword="null"/> for the seeded start event.</param>
/// <param name="ToNode">The node the run is at after the event.</param>
/// <param name="Status">The run's status after the event: a <see cref="WorkflowStatus"/> member's name.</param>
/// <param name="Outcome">The outcome the completed node reported, or <see langword="null"/> when it reported none.</param>
/// <param name="Error">The failure or cancellation reason, for a <c>Failed</c> or <c>Cancelled</c> event.</param>
/// <param name="Usage">
/// The token usage of the node a completion event closes, from <see cref="NodeResult.Usage"/>. <see langword="null"/>
/// for an event that ran no agent turn: the seeded start, a resume, a failure, a cancellation, and the completion of
/// a host-action node.
/// </param>
/// <param name="CreatedAt">When the event was recorded, on the store's clock.</param>
public sealed record WorkflowRunEvent(
    long Seq,
    string Kind,
    string? FromNode,
    string ToNode,
    string Status,
    string? Outcome,
    string? Error,
    TurnUsage? Usage,
    DateTimeOffset CreatedAt);
