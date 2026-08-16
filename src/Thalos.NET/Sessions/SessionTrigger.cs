namespace Thalos.Sessions;

public enum SessionTrigger
{
    Start,
    Complete,
    Fail,
    AwaitApproval,
    Approve,
    Deny,
    Close,
}
