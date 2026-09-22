using System.Text.Json;
using Npgsql;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// The outbox consumer these tests would otherwise have to hand-wave: reads the rows
/// <see cref="Thalos.Workflow.Orm.OrmWorkflowStore"/> actually enqueued under
/// <see cref="WorkflowDispatch.TypeName"/>, deserializes each one, and hands it to
/// <see cref="WorkflowNodeDispatcher.DispatchAsync"/> — the same three steps
/// <c>docs/workflow.md</c> tells a host to implement as an <c>IOutboxTypeDispatcher</c>.
/// </summary>
/// <remarks>
/// <para>
/// This exists so no test constructs a <see cref="WorkflowDispatchMessage"/> itself. A hand-built first
/// message is exactly what hid the gap where <c>StartAsync</c> wrote a run and never scheduled its start
/// node: every suite supplied the message production had forgotten to, and the runs advanced anyway. Driving
/// dispatch from the table means a store that stops enqueuing makes these tests do nothing at all, which is
/// what the assertions downstream then catch.
/// </para>
/// <para>
/// Deliberately not a faithful reimplementation of <c>ZeroAlloc.Outbox</c>'s worker: there is no retry, no
/// backoff and no dead-lettering here, because no test in this project asserts anything about those. A
/// dispatched row is deleted rather than marked, so a second drain in the same test sees only what the first
/// drain's dispatches themselves enqueued.
/// </para>
/// </remarks>
internal static class OutboxDrain
{
    /// <summary>
    /// Dispatches every pending workflow message, then everything those dispatches enqueue, until the outbox is
    /// empty. Returns how many messages were dispatched in total.
    /// </summary>
    /// <param name="maxMessages">
    /// A stop so a process that genuinely never terminates fails this helper loudly instead of hanging the test
    /// run. Chosen well above anything these fixtures produce; a drain that hits it is a bug in the process under
    /// test, not a tuning problem here.
    /// </param>
    public static async Task<int> DrainAsync(
        string connectionString, WorkflowNodeDispatcher dispatcher, int maxMessages = 64, CancellationToken ct = default)
    {
        var dispatched = 0;

        while (await TakeNextAsync(connectionString, ct) is { } message)
        {
            if (++dispatched > maxMessages)
            {
                throw new InvalidOperationException(
                    $"Outbox drain dispatched more than {maxMessages} messages without the queue emptying — the process under test does not terminate.");
            }

            await dispatcher.DispatchAsync(message, ct);
        }

        return dispatched;
    }

    /// <summary>Dispatches exactly one pending message — for a test that wants to observe the run between steps.</summary>
    public static async Task<bool> DispatchNextAsync(
        string connectionString, WorkflowNodeDispatcher dispatcher, CancellationToken ct = default)
    {
        if (await TakeNextAsync(connectionString, ct) is not { } message)
        {
            return false;
        }

        await dispatcher.DispatchAsync(message, ct);
        return true;
    }

    /// <summary>Reads and deletes the oldest pending workflow-dispatch row, or returns null when there is none.</summary>
    public static async Task<WorkflowDispatchMessage?> TakeNextAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            DELETE FROM outboxmessages
            WHERE Id = (SELECT Id FROM outboxmessages WHERE TypeName = @typeName ORDER BY CreatedAt, Id LIMIT 1)
            RETURNING Payload
            """,
            connection);
        cmd.Parameters.AddWithValue("typeName", WorkflowDispatch.TypeName);

        var payload = await cmd.ExecuteScalarAsync(ct);
        return payload is byte[] bytes ? JsonSerializer.Deserialize<WorkflowDispatchMessage>(bytes) : null;
    }
}
