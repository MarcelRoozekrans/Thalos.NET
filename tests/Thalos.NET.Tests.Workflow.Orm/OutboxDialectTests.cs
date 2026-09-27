using System.Data.Async.Adapters;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Outbox;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// The default of <see cref="WorkflowOrmOptions.OutboxStoreFactory"/> builds an <c>IOutboxStore</c> spelled for
/// PostgreSQL. Enqueueing is the same SQL in every dialect, so an enqueue alone cannot tell the dialects apart; the
/// batch claim can. The PostgreSQL claim locks its batch with <c>FOR UPDATE SKIP LOCKED</c> and passes over a row
/// another transaction holds, where the SQLite claim the store's one-argument constructor selects has no such clause
/// and waits on that row, here until <c>lock_timeout</c> ends the wait with <c>55P03</c>.
/// This covers the factory's default only. That <see cref="OrmWorkflowStore"/> enqueues through the factory at all
/// is guarded by <c>OrmWorkflowStoreTests.CompleteNode_rolls_back_all_four_writes_when_the_enqueue_throws</c>, whose
/// replacement factory is the only thing that makes that test's enqueue throw.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OutboxDialectTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_default_outbox_store_claims_past_a_row_another_transaction_has_locked()
    {
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        var store = new OrmWorkflowStore(options, new OrmProcessDefinitionStore(options));
        foreach (var key in new[] { "locked", "free" })
        {
            (await store.StartAsync(
                new WorkflowStartRequest { Process = "manufacture", Version = 1, CorrelationKey = key, StartNode = "implement", InitialVariables = null, StartedBy = TestPrincipals.Starter },
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using var holder = new NpgsqlConnection(pg.ConnectionString);
        await holder.OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var lockOne = new NpgsqlCommand("SELECT Id FROM outboxmessages ORDER BY CreatedAt LIMIT 1 FOR UPDATE", holder, holding))
        {
            await lockOne.ExecuteScalarAsync();
        }

        await using var claimer = new NpgsqlConnection(pg.ConnectionString);
        await claimer.OpenAsync();
        await using (var bounded = new NpgsqlCommand("SET lock_timeout = '1s'", claimer))
        {
            // A claim that waits on the locked row gives up after a second with 55P03, instead of waiting for ever.
            await bounded.ExecuteNonQueryAsync();
        }

        var outbox = options.OutboxStoreFactory(claimer.AsAsync());

        var claiming = () => outbox.ClaimPendingAsync(2, new OutboxLease("dialect-test", TimeSpan.FromMinutes(5)), CancellationToken.None).AsTask();

        var claimed = (await claiming.Should().NotThrowAsync("a PostgreSQL claim skips the locked row instead of waiting for its holder")).Subject;
        claimed.Should().ContainSingle("only the row nobody holds is claimable")
            .Which.TypeName.Should().Be(WorkflowDispatch.TypeName);
    }
}
