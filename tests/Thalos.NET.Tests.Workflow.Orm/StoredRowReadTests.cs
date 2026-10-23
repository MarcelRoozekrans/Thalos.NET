using System.Globalization;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Rows that 0.18.0 already wrote must keep reading back after the store moves to generated JSON metadata. Each test
/// inserts a row by raw SQL holding the exact JSON 0.18.0 writes, as <see cref="OrmWorkflowJsonGoldenTests"/> pins it,
/// and reads it through the store's public API. Characterization tests: they pass on today's code.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class StoredRowReadTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Tricky = "a\"b <a & 'é' 🚀>\nline";

    private const string VisitsJson = """{"implement":1,"review":2}""";

    private const string VariablesJson = """
        {"s":"a\u0022b \u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E\nline","n":3,"i":7,"d":1.5,"b":true,"z":null,"l":["x","y"],"o":["x",1,false,null],"m":{"k":"v","n":2,"inner":[1.25]},"neg":-4,"negd":-2.5,"whole":2,"sum":0.30000000000000004,"tiny":1E-07,"e":{"x":[1,2],"y":"\u00E9"}}
        """;

    private const string ManifestJson = """
        {"nodes":{"implement":{"agentName":"implementer","agentId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","agentRevision":"rev-1","skillName":"manufacture-implement","skillHash":"hash-1"},"review":{"agentName":"reviewer","agentId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","agentRevision":null,"skillName":"code-review","skillHash":"hash-2"}},"documents":{"standing_instructions":"a\u0022b \u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E\nline"}}
        """;

    private const string PrincipalJson = """{"id":"alice","roles":["admin","dev"],"displayName":"Alice \u003C\u00E9\u003E"}""";

    private const string ResumeJson = """{"principal":{"id":"alice","roles":["admin","dev"],"displayName":"Alice \u003C\u00E9\u003E"},"signal":"approved"}""";

    private const string UsageJson = """{"inputTokens":120,"outputTokens":45,"modelId":"claude-test","cacheReadTokens":30,"cacheWriteTokens":12}""";

    private static readonly Guid RunId = new(0x6b29fc40, 0xca47, 0x1067, 0xb3, 0x1d, 0x00, 0xdd, 0x01, 0x06, 0x62, 0xda);

    private static readonly DateTimeOffset At = new(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);

    private static readonly RunPrincipal Principal = new("alice", ["admin", "dev"]) { DisplayName = "Alice <é>" };

    private static readonly AgentId Agent = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", CultureInfo.InvariantCulture);

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, new OrmProcessDefinitionStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }));
        await InsertRowsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Red: drop WorkflowOrmWebJsonContext's Web source-generation options; the camelCase manifest no
    // longer binds. Red: in DeserializeVariables, deserialize to Dictionary<string, object?> directly; the values are
    // then JsonElements.
    [Fact]
    public async Task A_stored_run_reads_back_to_the_same_WorkflowRun()
    {
        var run = await _store.FindAsync(RunId, CancellationToken.None);

        run.Should().BeEquivalentTo(new WorkflowRun
        {
            Id = RunId,
            Process = "manufacture",
            ProcessVersion = 3,
            CurrentNode = "review",
            CurrentSeq = 4,
            Status = WorkflowStatus.Running,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["implement"] = 1, ["review"] = 2 },
            Variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["s"] = Tricky,
                ["n"] = 3L,
                ["i"] = 7L,
                ["d"] = 1.5,
                ["b"] = true,
                ["z"] = null,
                ["l"] = new List<object?> { "x", "y" },
                ["o"] = new List<object?> { "x", 1L, false, null },
                ["m"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v", ["n"] = 2L, ["inner"] = new List<object?> { 1.25 } },
                ["neg"] = -4L,
                ["negd"] = -2.5,
                ["whole"] = 2L,
                ["sum"] = 0.1 + 0.2,
                ["tiny"] = 1e-7,
                ["e"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = new List<object?> { 1L, 2L }, ["y"] = "é" },
            },
            Manifest = new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
                {
                    ["implement"] = new NodePin("implementer", Agent, "rev-1", "manufacture-implement", "hash-1"),
                    ["review"] = new NodePin("reviewer", Agent, null, "code-review", "hash-2"),
                },
                Documents = new Dictionary<string, string>(StringComparer.Ordinal) { ["standing_instructions"] = Tricky },
            },
            StartedBy = Principal,
            LastResume = new RunResume(Principal, At, "approved"),
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        }, o => o.PreferringRuntimeMemberTypes());
    }

    // Red: drop WorkflowOrmWebJsonContext's Web source-generation options; usage then reads as zeros
    // and no model. Red: put [JsonPropertyName("cacheRead")] on TurnUsage.CacheReadTokens; the cache-read count then
    // reads as 0.
    [Fact]
    public async Task A_stored_event_reads_back_with_its_usage_and_actor()
    {
        var events = await _store.ListEventsAsync(RunId, CancellationToken.None);

        var stored = events.Should().ContainSingle().Subject;
        stored.Usage.Should().Be(new TurnUsage(120, 45, "claude-test") { CacheReadTokens = 30, CacheWriteTokens = 12 });
        stored.Actor.Should().BeEquivalentTo(Principal);
    }

    // Red: drop WorkflowOrmWebJsonContext's Web source-generation options; the starter then no longer
    // reads back.
    [Fact]
    public async Task A_stored_run_lists_with_its_starter()
    {
        var page = await _store.ListAsync(new WorkflowRunFilter(), CancellationToken.None);

        page.Runs.Should().ContainSingle().Which.StartedBy.Should().BeEquivalentTo(Principal);
    }

    private async Task InsertRowsAsync()
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using (var run = new NpgsqlCommand(
            """
            INSERT INTO workflow_run (id, process, process_version, correlation_key, current_node, current_seq, status, awaiting_signal, visits, variables, last_error, manifest, started_by, last_resumed_by, last_resumed_at, trace_parent)
            VALUES (@id, 'manufacture', 3, 'k:stored', 'review', 4, 'Running', NULL, @visits::jsonb, @variables::jsonb, NULL, @manifest::jsonb, @startedBy::jsonb, @resumedBy::jsonb, @at, '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01')
            """,
            connection))
        {
            run.Parameters.AddWithValue("id", RunId);
            run.Parameters.AddWithValue("visits", VisitsJson);
            run.Parameters.AddWithValue("variables", VariablesJson);
            run.Parameters.AddWithValue("manifest", ManifestJson);
            run.Parameters.AddWithValue("startedBy", PrincipalJson);
            run.Parameters.AddWithValue("resumedBy", ResumeJson);
            run.Parameters.AddWithValue("at", At);
            await run.ExecuteNonQueryAsync();
        }

        await using var stored = new NpgsqlCommand(
            """
            INSERT INTO workflow_run_event (run_id, seq, kind, from_node, to_node, status, awaiting_signal, outcome, variables, error, usage, actor)
            VALUES (@id, 3, 'Completed', 'implement', 'review', 'Running', NULL, 'done', @variables::jsonb, NULL, @usage::jsonb, @actor::jsonb)
            """,
            connection);
        stored.Parameters.AddWithValue("id", RunId);
        stored.Parameters.AddWithValue("variables", VariablesJson);
        stored.Parameters.AddWithValue("usage", UsageJson);
        stored.Parameters.AddWithValue("actor", PrincipalJson);
        await stored.ExecuteNonQueryAsync();
    }
}
