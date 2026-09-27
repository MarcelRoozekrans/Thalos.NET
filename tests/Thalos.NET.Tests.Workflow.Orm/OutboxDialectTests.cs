using System.Data.Async.Adapters;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Outbox;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// The <c>IOutboxStore</c> <see cref="OrmWorkflowStore"/> builds when no test overrides
/// <see cref="WorkflowOrmOptions.OutboxStoreFactory"/> is spelled for PostgreSQL. Enqueueing is the same SQL in every
/// dialect, so an enqueue alone cannot tell the dialects apart; the batch claim can. The PostgreSQL claim locks its
/// batch with <c>FOR UPDATE SKIP LOCKED</c> and passes over a row another transaction holds, where the SQLite claim
/// the store's one-argument constructor selects has no such clause and waits on that row until its holder finishes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OutboxDialectTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>Far longer than a claim over two rows takes, and far shorter than the lock is held for.</summary>
    private static readonly TimeSpan ClaimBudget = TimeSpan.FromSeconds(10);

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
        var outbox = options.OutboxStoreFactory(claimer.AsAsync());

        Task<IReadOnlyList<OutboxEntry>> claim;
        Task finished;
        try
        {
            claim = outbox.ClaimPendingAsync(2, new OutboxLease("dialect-test", TimeSpan.FromMinutes(5)), CancellationToken.None).AsTask();
            finished = await Task.WhenAny(claim, Task.Delay(ClaimBudget));
        }
        finally
        {
            // Releases the row lock either way, so a claim that is waiting on it finishes and the connections close.
            await holding.RollbackAsync();
        }

        var claimed = await claim;

        finished.Should().BeSameAs(claim, "a PostgreSQL claim skips the locked row instead of waiting for its holder");
        claimed.Should().ContainSingle("only the row nobody holds is claimable")
            .Which.TypeName.Should().Be(WorkflowDispatch.TypeName);
    }
}
