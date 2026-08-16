namespace Thalos;

/// <summary>Lifecycle state of a session. Transitions are owned by the runtime's state machine.</summary>
public enum SessionState
{
    /// <summary>No turn in progress; accepts a new turn.</summary>
    Idle,

    /// <summary>A turn is executing; further turns are rejected with <see cref="AgentErrorCode.SessionBusy"/>.</summary>
    Running,

    /// <summary>Entered when a tool call needs human approval; left by Approve, Deny or Close.</summary>
    AwaitingApproval,

    /// <summary>Terminal: the session accepts no more turns.</summary>
    Closed,
}
