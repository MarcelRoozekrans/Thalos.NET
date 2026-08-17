using Thalos.Runtime;

namespace Thalos.Memory;

/// <summary>Publishes memory events into the current turn (streamed + hub) or, outside a turn, straight to the hub with default ids.</summary>
internal static class MemoryEvents
{
    public static ValueTask PublishAsync(AgentEventHub hub, Func<SessionId, TurnId, AgentEvent> make, CancellationToken ct)
    {
        var scope = TurnScope.Current;
        return scope is null ? hub.PublishAsync(make(default, default), ct) : scope.PublishAsync(make(scope.SessionId, scope.TurnId), ct);
    }
}
