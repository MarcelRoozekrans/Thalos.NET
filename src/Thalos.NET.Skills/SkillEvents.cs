using Thalos.Runtime;

namespace Thalos.Skills;

/// <summary>Publishes skill events into the current turn (streamed + hub) or, outside a turn, straight to the hub with default ids.</summary>
/// <remarks>A deliberate copy of the memory package's equivalent: the two packages must not depend on each other.</remarks>
internal static class SkillEvents
{
    public static ValueTask PublishAsync(AgentEventHub hub, Func<SessionId, TurnId, AgentEvent> make, CancellationToken ct)
    {
        var scope = TurnScope.Current;
        return scope is null ? hub.PublishAsync(make(default, default), ct) : scope.PublishAsync(make(scope.SessionId, scope.TurnId), ct);
    }
}
