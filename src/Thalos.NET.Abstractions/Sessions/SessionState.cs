namespace Thalos;

/// <summary>Lifecycle state of a session. Transitions are owned by the runtime's state machine.</summary>
public enum SessionState
{
    Idle,
    Running,
    AwaitingApproval,
    Closed,
}
