using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
///     <see cref="OrmWorkflowStore"/> as <see cref="IWorkflowRunQuery"/>: newest first, filtered, keyset-paged, and
///     never the run's variables.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class WorkflowRunQueryTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new OrmWorkflowStore(
            new WorkflowOrmOptions { ConnectionString = pg.ConnectionString },
            WorkflowRunSeeds.NewDefinitionStore(pg.ConnectionString));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Three runs created a minute apart come back newest first, with their summary fields.
    ///     Red: <c>ORDER BY created_at ASC</c> in ListAsync's SQL.
    ///     Red: map <c>current_node</c> from the <c>process</c> column.
    ///     Red: drop <c>started_by</c> from the summary SELECT.
    /// </summary>
    [Fact]
    public async Task Runs_come_back_newest_first_with_their_summary()
    {
        var oldest = await StartAsync("p-order", "c1", minutesAgo: 3);
        var middle = await StartAsync("p-order", "c2", minutesAgo: 2);
        var newest = await StartAsync("p-order", "c3", minutesAgo: 1);

        var page = await _store.ListAsync(new WorkflowRunFilter { Process = "p-order" }, CancellationToken.None);

        page.Runs.Select(r => r.Id).Should().Equal(newest, middle, oldest);
        page.Runs[0].CurrentNode.Should().Be("implement");
        page.Runs[0].StartedBy!.Id.Should().Be(TestPrincipals.Starter.Id);
        page.Next.Should().BeNull();
    }

    /// <summary>
    ///     The status and process filters each narrow the list.
    ///     Red: drop the status predicate.
    ///     Red: drop the process predicate.
    /// </summary>
    [Fact]
    public async Task Status_and_process_filter_the_list()
    {
        var running = await StartAsync("p-filter", "c1", minutesAgo: 2);
        var cancelled = await StartAsync("p-filter", "c2", minutesAgo: 1);
        await _store.CancelAsync(cancelled, "test", CancellationToken.None);
        await StartAsync("p-other", "c3", minutesAgo: 1);

        var byStatus = await _store.ListAsync(
            new WorkflowRunFilter { Process = "p-filter", Status = WorkflowStatus.Cancelled }, CancellationToken.None);
        var byProcess = await _store.ListAsync(new WorkflowRunFilter { Process = "p-filter" }, CancellationToken.None);

        byStatus.Runs.Select(r => r.Id).Should().Equal(cancelled);
        byProcess.Runs.Select(r => r.Id).Should().BeEquivalentTo([running, cancelled]);
    }

    /// <summary>
    ///     Paging two at a time walks five runs once each, in order, and the last page has no next cursor, including
    ///     two runs created in the same instant, which the id orders.
    ///     Red: <c>(created_at, id) &lt;=</c> instead of <c>&lt;</c>: the cursor row repeats.
    ///     Red: read <c>Take</c> rows instead of <c>Take + 1</c>: the last full page claims a next page that is empty,
    ///     or a final page loses its cursor.
    ///     Red: <c>ORDER BY created_at DESC</c> without <c>id DESC</c>: same-instant runs come back in either order.
    /// </summary>
    [Fact]
    public async Task Keyset_paging_walks_every_run_once()
    {
        for (var i = 0; i < 5; i++)
        {
            await StartAsync("p-page", $"c{i}", minutesAgo: i < 2 ? 10 : 10 - i);
        }

        var expected = await OrderedIdsAsync("p-page");
        var seen = new List<Guid>();
        WorkflowRunCursor? after = null;
        var pages = 0;
        do
        {
            var page = await _store.ListAsync(
                new WorkflowRunFilter { Process = "p-page", Take = 2, After = after }, CancellationToken.None);
            foreach (var run in page.Runs)
            {
                seen.Add(run.Id);
            }

            after = page.Next;
            pages++;
        }
        while (after is not null);

        seen.Should().Equal(expected);
        pages.Should().Be(3);
    }

    /// <summary>
    ///     A take of 0 or 101 throws.
    ///     Red: drop the lower bound check. Red: drop the upper bound check.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task A_take_outside_the_bounds_throws(int take)
    {
        var act = async () => await _store.ListAsync(new WorkflowRunFilter { Take = take }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>
    ///     A summary has exactly the nine properties of the contract, so variables or an error cannot be added to the
    ///     listing without this test changing.
    ///     Red: add a <c>Variables</c> or <c>LastError</c> property to <see cref="WorkflowRunSummary"/>.
    /// </summary>
    [Fact]
    public void A_summary_carries_no_variables_and_no_error()
    {
        typeof(WorkflowRunSummary).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            ["Id", "Process", "ProcessVersion", "Status", "CurrentNode", "AwaitingSignal", "StartedBy", "CreatedAt", "UpdatedAt"]);
    }

    private async Task<Guid> StartAsync(string process, string correlation, int minutesAgo)
    {
        var id = await WorkflowRunSeeds.StartAsync(_store, process, correlation);
        await WorkflowRunSeeds.SetCreatedAtAsync(pg.ConnectionString, id, Base.AddMinutes(-minutesAgo));
        return id;
    }

    private async Task<List<Guid>> OrderedIdsAsync(string process)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM workflow_run WHERE process = @p ORDER BY created_at DESC, id DESC", connection);
        cmd.Parameters.AddWithValue("p", process);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
