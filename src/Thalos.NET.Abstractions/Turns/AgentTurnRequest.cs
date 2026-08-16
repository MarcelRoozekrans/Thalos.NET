using ZeroAlloc.Authorization;
using ZeroAlloc.Validation;

namespace Thalos;

/// <summary>One user message for a session. <see cref="Caller"/> is never inferred by Thalos — the channel supplies it.</summary>
[Validate]
public sealed record AgentTurnRequest(
    SessionId SessionId,
    [property: NotEmpty] string Text,
    ISecurityContext Caller);
