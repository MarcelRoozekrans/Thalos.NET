using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Tools;

/// <summary>
/// The enforcement point for tool authorization. Runs inside MAF's function-invocation loop, so it is
/// guaranteed to execute before the tool regardless of how the chat-client pipeline is ordered.
/// </summary>
/// <remarks>
/// <para>
/// On every invocation: publishes <see cref="ToolCallRequestedNotification"/>, asks the <see cref="IToolAuthorizer"/>
/// with the ambient <see cref="TurnScope.Caller"/> (anonymous when no turn scope is active), then either returns a
/// <c>"Tool call denied: {reason}"</c> string to the model (the turn continues) or runs the inner function and publishes
/// <see cref="ToolCallCompletedNotification"/>. Tool events and a <see cref="ToolCallSummary"/> are pushed into the turn scope.
/// </para>
/// <para>
/// Notifications are awaited inline on the tool hot path — <see cref="IAgentNotificationPublisher"/> implementations
/// must be non-blocking (queue and return). An exception thrown by the publisher propagates and prevents the tool from running.
/// </para>
/// <para>
/// Exceptions from the tool are audited (Completed with <c>Succeeded=false</c>, summary, Finished event) and rethrown —
/// including <see cref="OperationCanceledException"/>s that do not stem from the ambient token (tool-internal timeouts).
/// Only cancellation of the ambient token itself is passed through unaudited.
/// </para>
/// </remarks>
/// <param name="inner">The tool being guarded; its description and schema are exposed unchanged.</param>
/// <param name="qualifiedName">The name exposed to the model (<c>{source}__{tool}</c>).</param>
/// <param name="authorizer">Decides per call whether the ambient caller may run the tool.</param>
/// <param name="publisher">Receives the requested/denied/completed audit notifications.</param>
/// <param name="clock">Timestamps notifications and measures tool duration.</param>
/// <param name="logger">Optional diagnostics logger; defaults to a null logger.</param>
public sealed partial class AuthorizingAIFunction(
    AIFunction inner,
    string qualifiedName,
    IToolAuthorizer authorizer,
    IAgentNotificationPublisher publisher,
    TimeProvider clock,
    ILogger<AuthorizingAIFunction>? logger = null) : DelegatingAIFunction(inner)
{
    private const int PreviewLength = 200;
    private const string AuthorizationErrorReason = "authorization error";

    private static readonly JsonSerializerOptions CompactJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private readonly ILogger<AuthorizingAIFunction> _logger = logger ?? NullLogger<AuthorizingAIFunction>.Instance;

    /// <summary>The qualified tool name (<c>{source}__{tool}</c>) exposed to the model; description and schema come from the inner function.</summary>
    public override string Name => qualifiedName;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = TurnScope.Current;
        if (scope is null)
        {
            LogOutsideTurnScope(_logger, qualifiedName);
        }

        var caller = scope?.Caller ?? AnonymousSecurityContext.Instance;
        var sessionId = scope?.SessionId ?? default;
        var turnId = scope?.TurnId ?? default;
        var callId = ToolCallId.New();
        var argsJson = JsonSerializer.SerializeToElement(arguments, CompactJson);
        var argsText = argsJson.GetRawText();

        await publisher.PublishAsync(new ToolCallRequestedNotification(sessionId, turnId, callId, qualifiedName, argsText, caller.Id, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        if (scope is not null)
        {
            await scope.PublishAsync(new ToolCallStartedEvent(sessionId, turnId, callId, qualifiedName, argsText), cancellationToken).ConfigureAwait(false);
        }

        ToolAuthorizationDecision decision;
        try
        {
            decision = await authorizer.AuthorizeAsync(caller, qualifiedName, argsJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogAuthorizerThrew(_logger, qualifiedName, ex.Message, ex);
            decision = ToolAuthorizationDecision.Deny(AuthorizationErrorReason);
        }

        if (!decision.Allowed)
        {
            var reason = decision.Reason ?? "denied";
            await publisher.PublishAsync(new ToolCallDeniedNotification(sessionId, turnId, callId, qualifiedName, reason, caller.Id, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await FinishAsync(scope, sessionId, turnId, callId, argsText, succeeded: false, preview: $"denied: {reason}", TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            return $"Tool call denied: {reason}";
        }

        var start = clock.GetTimestamp();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            var elapsed = clock.GetElapsedTime(start);
            var preview = Preview(result);
            await publisher.PublishAsync(new ToolCallCompletedNotification(sessionId, turnId, callId, qualifiedName, true, elapsed, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await FinishAsync(scope, sessionId, turnId, callId, argsText, succeeded: true, preview, elapsed, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Audit with CancellationToken.None: the ambient token may be cancelled by now and the audit trail must still land.
            var elapsed = clock.GetElapsedTime(start);
            LogToolFailed(_logger, qualifiedName, ex.Message, ex);
            await publisher.PublishAsync(new ToolCallCompletedNotification(sessionId, turnId, callId, qualifiedName, false, elapsed, clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            await FinishAsync(scope, sessionId, turnId, callId, argsText, succeeded: false, preview: ex.GetType().Name, elapsed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask FinishAsync(TurnScope? scope, SessionId sessionId, TurnId turnId, ToolCallId callId, string argsText, bool succeeded, string? preview, TimeSpan elapsed, CancellationToken ct)
    {
        if (scope is null)
        {
            return;
        }

        scope.RecordToolCall(new ToolCallSummary(callId, qualifiedName, argsText, succeeded, preview, elapsed));
        await scope.PublishAsync(new ToolCallFinishedEvent(sessionId, turnId, callId, qualifiedName, succeeded, preview, elapsed), ct).ConfigureAwait(false);
    }

    private static string? Preview(object? result)
    {
        if (result is null)
        {
            return null;
        }

        var text = result is JsonElement je ? je.GetRawText() : result.ToString();
        if (text is null || text.Length <= PreviewLength)
        {
            return text;
        }

        var cut = PreviewLength;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // never split a surrogate pair
        }

        return string.Concat(text.AsSpan(0, cut), "…");
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Warning, Message = "Authorizer threw for tool {Tool}: {Error}")]
    private static partial void LogAuthorizerThrew(ILogger logger, string tool, string error, Exception exception);

    [LoggerMessage(EventId = 112, Level = LogLevel.Warning, Message = "Tool {Tool} failed: {Error}")]
    private static partial void LogToolFailed(ILogger logger, string tool, string error, Exception exception);

    [LoggerMessage(EventId = 113, Level = LogLevel.Debug, Message = "Tool {Tool} invoked outside a turn scope; caller is anonymous")]
    private static partial void LogOutsideTurnScope(ILogger logger, string tool);
}
