using Thalos.Sessions;

namespace Thalos.Tests.Unit.Sessions;

public sealed class AgentSessionMachineTests
{
    [Fact]
    public void Starts_idle()
    {
        new AgentSessionMachine().Current.Should().Be(SessionState.Idle);
    }

    [Theory]
    // from Idle
    [InlineData(SessionState.Idle, SessionTrigger.Start, true, SessionState.Running)]
    [InlineData(SessionState.Idle, SessionTrigger.Close, true, SessionState.Closed)]
    [InlineData(SessionState.Idle, SessionTrigger.Complete, false, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionTrigger.Fail, false, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionTrigger.AwaitApproval, false, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionTrigger.Approve, false, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionTrigger.Deny, false, SessionState.Idle)]
    // from Running
    [InlineData(SessionState.Running, SessionTrigger.Complete, true, SessionState.Idle)]
    [InlineData(SessionState.Running, SessionTrigger.Fail, true, SessionState.Idle)]
    [InlineData(SessionState.Running, SessionTrigger.AwaitApproval, true, SessionState.AwaitingApproval)]
    [InlineData(SessionState.Running, SessionTrigger.Start, false, SessionState.Running)]
    [InlineData(SessionState.Running, SessionTrigger.Close, false, SessionState.Running)]
    [InlineData(SessionState.Running, SessionTrigger.Approve, false, SessionState.Running)]
    [InlineData(SessionState.Running, SessionTrigger.Deny, false, SessionState.Running)]
    // from AwaitingApproval
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.Approve, true, SessionState.Running)]
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.Deny, true, SessionState.Idle)]
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.Close, true, SessionState.Closed)]
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.Start, false, SessionState.AwaitingApproval)]
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.Complete, false, SessionState.AwaitingApproval)]
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.Fail, false, SessionState.AwaitingApproval)]
    [InlineData(SessionState.AwaitingApproval, SessionTrigger.AwaitApproval, false, SessionState.AwaitingApproval)]
    // from Closed (terminal)
    [InlineData(SessionState.Closed, SessionTrigger.Start, false, SessionState.Closed)]
    [InlineData(SessionState.Closed, SessionTrigger.Close, false, SessionState.Closed)]
    public void Transitions(SessionState from, SessionTrigger trigger, bool accepted, SessionState expected)
    {
        var m = new AgentSessionMachine(from);
        m.TryFire(trigger).Should().Be(accepted);
        m.Current.Should().Be(expected);
    }
}
