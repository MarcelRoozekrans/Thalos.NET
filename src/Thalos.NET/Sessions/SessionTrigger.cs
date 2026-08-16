namespace Thalos.Sessions;

/// <summary>Triggers of <see cref="AgentSessionMachine"/>; see the transition table on that class.</summary>
public enum SessionTrigger
{
    /// <summary>A turn claims the session: Idle → Running.</summary>
    Start,

    /// <summary>The turn finished successfully: Running → Idle.</summary>
    Complete,

    /// <summary>The turn failed or was cancelled (turn discarded): Running → Idle.</summary>
    Fail,

    /// <summary>The turn parks for a human decision: Running → AwaitingApproval (reserved for a later phase).</summary>
    AwaitApproval,

    /// <summary>The pending decision was approved, the turn resumes: AwaitingApproval → Running (reserved).</summary>
    Approve,

    /// <summary>The pending decision was denied, the turn ends: AwaitingApproval → Idle (reserved).</summary>
    Deny,

    /// <summary>The session is closed for good: Idle or AwaitingApproval → Closed (terminal). Not valid while Running.</summary>
    Close,
}
