using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Tools;

/// <summary>
/// The enforcement point for tool authorization. Runs inside MAF's function-invocation loop, so it is
/// guaranteed to execute before the tool regardless of how the chat-client pipeline is ordered.
/// </summary>
public sealed class AuthorizingAIFunction(
    AIFunction inner,
    string qualifiedName,
    IToolAuthorizer authorizer,
    IAgentNotificationPublisher publisher,
    TimeProvider clock) : DelegatingAIFunction(inner)
{
    private const int PreviewLength = 200;

    public override string Name => qualifiedName;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = TurnScope.Current;
        var caller = scope?.Caller ?? AnonymousSecurityContext.Instance;
        var sessionId = scope?.SessionId ?? default;
        var turnId = scope?.TurnId ?? default;
        var callId = ToolCallId.New();
        var argsJson = JsonSerializer.SerializeToElement(arguments, AIJsonUtilities.DefaultOptions);
        var argsText = argsJson.GetRawText();
        var now = clock.GetUtcNow();

        await publisher.PublishAsync(new ToolCallRequestedNotification(sessionId, turnId, callId, qualifiedName, argsText, caller.Id, now), cancellationToken).ConfigureAwait(false);
        if (scope is not null)
        {
            await scope.PublishAsync(new ToolCallStartedEvent(sessionId, turnId, callId, qualifiedName, argsText), cancellationToken).ConfigureAwait(false);
        }

        var decision = await authorizer.AuthorizeAsync(caller, qualifiedName, argsJson, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            var reason = decision.Reason ?? "denied";
            await publisher.PublishAsync(new ToolCallDeniedNotification(sessionId, turnId, callId, qualifiedName, reason, caller.Id, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await FinishAsync(scope, sessionId, turnId, callId, argsText, succeeded: false, preview: $"denied: {reason}", TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            return $"Tool call denied: {reason}";
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var preview = Preview(result);
            await publisher.PublishAsync(new ToolCallCompletedNotification(sessionId, turnId, callId, qualifiedName, true, sw.Elapsed, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await FinishAsync(scope, sessionId, turnId, callId, argsText, succeeded: true, preview, sw.Elapsed, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            await publisher.PublishAsync(new ToolCallCompletedNotification(sessionId, turnId, callId, qualifiedName, false, sw.Elapsed, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await FinishAsync(scope, sessionId, turnId, callId, argsText, succeeded: false, preview: ex.Message, sw.Elapsed, cancellationToken).ConfigureAwait(false);
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
        return text is { Length: > PreviewLength } ? text[..PreviewLength] + "…" : text;
    }
}
