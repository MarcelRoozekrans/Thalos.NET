using System.Runtime.InteropServices;
using ZeroAlloc.Mediator;

namespace Thalos;

// Terminology: OwnerId = the security-context id that created (owns) the session;
//              CallerId = the security-context id that issued the specific turn (may differ from the owner).

/// <summary>A session was created for <paramref name="AgentId"/>, owned by <paramref name="OwnerId"/>.</summary>
public readonly record struct SessionCreatedNotification(SessionId SessionId, AgentId AgentId, string OwnerId, DateTimeOffset At) : INotification;

/// <summary>A session was closed; it accepts no further turns.</summary>
[StructLayout(LayoutKind.Auto)] // MA0008: all-blittable fields — make the layout choice explicit
public readonly record struct SessionClosedNotification(SessionId SessionId, DateTimeOffset At) : INotification;

/// <summary>A turn began; <paramref name="CallerId"/> is who issued the turn (not necessarily the session owner).</summary>
public readonly record struct TurnStartedNotification(SessionId SessionId, TurnId TurnId, AgentId AgentId, string CallerId, DateTimeOffset At) : INotification;

/// <summary>A turn finished successfully with the summed <paramref name="Usage"/> and wall-clock <paramref name="Elapsed"/>.</summary>
public readonly record struct TurnCompletedNotification(SessionId SessionId, TurnId TurnId, TurnUsage Usage, TimeSpan Elapsed, DateTimeOffset At) : INotification;

/// <summary>
/// A turn failed with <paramref name="Error"/>; the session returns to Idle and the turn is discarded. <paramref name="Usage"/> is
/// the token usage of the model round-trips that completed before the failure (zero when the failure preceded the first
/// round-trip — e.g. validation, authorization, quarantine of the request itself), so hosts can bill failed/quarantined turns.
/// </summary>
public readonly record struct TurnFailedNotification(SessionId SessionId, TurnId TurnId, AgentError Error, DateTimeOffset At, TurnUsage Usage = default) : INotification;

/// <summary>The model requested a tool call on behalf of <paramref name="CallerId"/> (published before authorization).</summary>
public readonly record struct ToolCallRequestedNotification(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, string ArgumentsJson, string CallerId, DateTimeOffset At) : INotification;

/// <summary>The tool authorizer refused a call for <paramref name="CallerId"/> with <paramref name="Reason"/>; the tool did not run.</summary>
public readonly record struct ToolCallDeniedNotification(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, string Reason, string CallerId, DateTimeOffset At) : INotification;

/// <summary>An authorized tool call ran to completion (<paramref name="Succeeded"/> tells whether it threw).</summary>
public readonly record struct ToolCallCompletedNotification(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, bool Succeeded, TimeSpan Elapsed, DateTimeOffset At) : INotification;
