namespace Thalos.Workflow;

/// <summary>
///     Lists workflow runs for an operator console: newest first by creation time, then by id, one keyset page at a
///     time. A summary never carries the run's variables or its last error, so no work content can leave through a
///     listing.
/// </summary>
public interface IWorkflowRunQuery
{
    /// <summary>One page of the runs that match <paramref name="filter"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="WorkflowRunFilter.Take"/> is outside 1 to <see cref="WorkflowRunFilter.MaxTake"/>.</exception>
    ValueTask<WorkflowRunPage> ListAsync(WorkflowRunFilter filter, CancellationToken ct);
}

/// <summary>Which runs <see cref="IWorkflowRunQuery.ListAsync"/> returns, and how many.</summary>
public sealed record WorkflowRunFilter
{
    /// <summary>The largest page <see cref="IWorkflowRunQuery.ListAsync"/> returns.</summary>
    public const int MaxTake = 100;

    /// <summary>Only runs in this status, or every status when <see langword="null"/>.</summary>
    public WorkflowStatus? Status { get; init; }

    /// <summary>Only runs of this process, compared exactly, or every process when <see langword="null"/>.</summary>
    public string? Process { get; init; }

    /// <summary>Only runs strictly older than this cursor, or from the newest run when <see langword="null"/>.</summary>
    public WorkflowRunCursor? After { get; init; }

    /// <summary>How many runs to return at most, 1 to <see cref="MaxTake"/>.</summary>
    public int Take { get; init; } = 25;
}

/// <summary>A position in the newest-first order: the creation time and id of the last run a page returned.</summary>
/// <param name="CreatedAt">The run's creation time, as the store recorded it.</param>
/// <param name="Id">The run's id, which breaks ties between runs created in the same instant.</param>
public sealed record WorkflowRunCursor(DateTimeOffset CreatedAt, Guid Id);

/// <summary>One page of runs.</summary>
/// <param name="Runs">The runs, newest first.</param>
/// <param name="Next">The cursor for the next page, or <see langword="null"/> when no older run matches.</param>
public sealed record WorkflowRunPage(IReadOnlyList<WorkflowRunSummary> Runs, WorkflowRunCursor? Next);

/// <summary>What a console shows about one run without opening it.</summary>
public sealed record WorkflowRunSummary
{
    /// <summary>The run's identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>The process the run executes.</summary>
    public required string Process { get; init; }

    /// <summary>The version of that process.</summary>
    public required int ProcessVersion { get; init; }

    /// <summary>The run's lifecycle status.</summary>
    public required WorkflowStatus Status { get; init; }

    /// <summary>The node the run is positioned at.</summary>
    public required string CurrentNode { get; init; }

    /// <summary>The signal a parked run waits on, or <see langword="null"/>.</summary>
    public string? AwaitingSignal { get; init; }

    /// <summary>Who started the run, or <see langword="null"/> for a run started before 0.11.0.</summary>
    public RunPrincipal? StartedBy { get; init; }

    /// <summary>When the run was created, on the store's clock.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the run last changed, on the store's clock.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
