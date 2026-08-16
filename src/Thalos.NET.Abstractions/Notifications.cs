using System.Runtime.InteropServices;
using ZeroAlloc.Mediator;

namespace Thalos;

public readonly record struct SessionCreatedNotification(SessionId SessionId, AgentId AgentId, string OwnerId, DateTimeOffset At) : INotification;
[StructLayout(LayoutKind.Auto)] // MA0008: all-blittable fields — make the layout choice explicit
public readonly record struct SessionClosedNotification(SessionId SessionId, DateTimeOffset At) : INotification;
public readonly record struct TurnStartedNotification(SessionId SessionId, TurnId TurnId, AgentId AgentId, string CallerId, DateTimeOffset At) : INotification;
public readonly record struct TurnCompletedNotification(SessionId SessionId, TurnId TurnId, TurnUsage Usage, TimeSpan Elapsed, DateTimeOffset At) : INotification;
public readonly record struct TurnFailedNotification(SessionId SessionId, TurnId TurnId, AgentError Error, DateTimeOffset At) : INotification;
public readonly record struct ToolCallRequestedNotification(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, string ArgumentsJson, string CallerId, DateTimeOffset At) : INotification;
public readonly record struct ToolCallDeniedNotification(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, string Reason, string CallerId, DateTimeOffset At) : INotification;
public readonly record struct ToolCallCompletedNotification(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, bool Succeeded, TimeSpan Elapsed, DateTimeOffset At) : INotification;
