using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// An <see cref="IWorkflowHostAction"/> that counts its calls and does whatever <paramref name="behaviour"/> says
/// with the token it is actually handed. The static factories cover the shapes the dispatch tests need: a fixed
/// result, a failure, an exception, and a wait that ends only when the dispatch token is cancelled.
/// </summary>
internal sealed class RecordingAction(string name, Func<CancellationToken, ValueTask<Result<HostActionResult>>> behaviour) : IWorkflowHostAction
{
    public string Name => name;

    public int Calls { get; private set; }

    public ValueTask<Result<HostActionResult>> RunAsync(WorkflowRun run, ProcessNode node, CancellationToken ct)
    {
        Calls++;
        return behaviour(ct);
    }

    public static RecordingAction Returning(string name, HostActionResult result) =>
        new(name, _ => ValueTask.FromResult(Result<HostActionResult>.Success(result)));

    public static RecordingAction Failing(string name, string error) =>
        new(name, _ => ValueTask.FromResult(Result<HostActionResult>.Failure(error)));

    public static RecordingAction Throwing(string name, Exception exception) =>
        new(name, _ => throw exception);

    /// <summary>
    /// Waits on the token the dispatcher hands it until that token is cancelled, so a test can tell "the
    /// dispatcher forwarded its own ct" apart from "it passed some other token", which an action that throws a
    /// bare <see cref="OperationCanceledException"/> cannot.
    /// </summary>
    public static RecordingAction WaitingForCancellation(string name) =>
        new(name, async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<HostActionResult>.Failure("unreachable: the delay only ends by cancellation");
        });
}
