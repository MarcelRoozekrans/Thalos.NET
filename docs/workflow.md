# Wiring the workflow engine

From `dotnet add package` to a run that actually moves. `Thalos.NET.Workflow` is a graph model, a loader, a
validator, a pure interpreter and a node dispatcher; `Thalos.NET.Workflow.Orm` persists runs on PostgreSQL with a
transactional outbox. Neither package hosts anything. A run only advances once a host has assembled seven pieces,
and `AddWorkflowOrm` registers two of them — this page is the other five.

## What you are assembling

| Piece | Registered by `AddWorkflowOrm`? | What it does |
| --- | --- | --- |
| `IWorkflowStore` (`OrmWorkflowStore`) | yes | Writes the run, the event log and the next dispatch in one transaction |
| `IProcessDefinitionStore` (`OrmProcessDefinitionStore`, cached) | yes | The one answer to "what is this process, at this version" |
| `IWorkflowReferenceResolver` | **no** | Turns an `agent:`/`skill:` name in the YAML into something that exists |
| `WorkflowNodeDispatcher` | **no** | Runs one node's agent turn and persists the transition |
| An outbox consumer for `WorkflowDispatch.TypeName` | **no** | Takes a queued message off the table and calls `DispatchAsync` |
| `WorkflowRunReconciler` on a timer | **no** | Terminates runs nothing is advancing any more |
| `IProcessDefinitionSource` + `ProcessDefinitionSync` | **no** | Gets your `.process.yaml` files into the table |

Nothing in either package is a hosted service except the schema initializer. That is deliberate — a library that
starts its own timers and pollers fights whatever hosting model you already have — but it does mean a host that
stops after `AddWorkflowOrm` has a database that records runs and nothing that moves them.

## 1. Packages and the store

```bash
dotnet add package Thalos.NET.Workflow
dotnet add package Thalos.NET.Workflow.Orm
```

```csharp
using Thalos;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

services.AddThalos(thalos => thalos
    .UseAnthropic(configuration)
    .UseInMemorySessionStore()
    .UseSkills(o => o.Roots.Add(Path.Combine(AppContext.BaseDirectory, "skills")))
    .AddAgent(new AgentDefinition { Id = AgentId.New(), Name = "backend", Instructions = "..." })
    .AddWorkflowOrm(o => o.ConnectionString = configuration.GetConnectionString("workflow")!));
```

`AddWorkflowOrm` registers `IWorkflowStore`, `IProcessDefinitionStore` (an `OrmProcessDefinitionStore` behind
`CachingProcessDefinitionStore`), the options object, and — while `WorkflowOrmOptions.EnsureSchemaOnStartup` is
left on, which it is by default — a hosted service that applies the outbox and workflow migrations before the host
accepts work. Read [`release.md`](release.md#schema-migrations-and-rolling-deploys) before leaving that on across a
rolling deploy: migration 1004 is not backward compatible with pre-1004 code.

## 2. The reference resolver

`WorkflowReferenceResolver` resolves `agent:` over `IAgentCatalog` and `skill:` over `ISkillStore`, both of which
`AddThalos` already provides. It is not registered for you, because a host that resolves agent names some other way
should be able to say so:

```csharp
services.AddSingleton<IWorkflowReferenceResolver, WorkflowReferenceResolver>();
```

Agent names are matched case-insensitively against `AgentDefinition.Name`. A skill whose file has disappeared does
not count as existing.

## 3. The node dispatcher

```csharp
services.AddSingleton(sp => new WorkflowNodeDispatcher(
    sp.GetRequiredService<IWorkflowStore>(),
    sp.GetRequiredService<ISubagentRunner>(),
    sp.GetRequiredService<IWorkflowReferenceResolver>(),
    sp.GetRequiredService<IProcessDefinitionStore>(),
    resolveCaller: run => new WorkflowCaller(run)));
```

The last argument is the one only you can supply: the `ISecurityContext` each run executes as. The dispatcher never
inspects what comes back from it — it forwards the value into `SubagentRunRequest.Caller`, and Thalos's tool
authorization does the rest. A host with no per-run identity can return one fixed service principal; a host that
runs workflows on behalf of people should return theirs, because that is what decides which tools the node's agent
is allowed to call.

```csharp
using ZeroAlloc.Authorization;   // ISecurityContext lives here, not in Thalos

sealed class WorkflowCaller(WorkflowRun run) : ISecurityContext
{
    public string Id => $"workflow:{run.Process}:{run.Id}";
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "workflow" };
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
```

## 4. The outbox consumer — the piece with no default

`OrmWorkflowStore` enqueues a `WorkflowDispatchMessage` under `WorkflowDispatch.TypeName`
(`"thalos.workflow.node-dispatch"`) in the same transaction as every transition that leaves a run `Running`,
including the very first one that `StartAsync` writes. Nothing in either package takes those rows back off the
table. Until you host a consumer, every run you start is a row in `outboxmessages` that nobody reads.

ZeroAlloc.Outbox's `OutboxWorkerService` is the poller; it matches a queued row's `TypeName` against the
`IOutboxTypeDispatcher` implementations registered in DI and dead-letters a row whose type nothing claims. The
generated `[OutboxMessage]` route does not apply here — `WorkflowDispatchMessage` lives in the dependency-free core
package, carries no attribute, and is enqueued under a hand-chosen type name rather than a generator-derived one —
so bind to the type name directly:

```csharp
using System.Text.Json;
using ZeroAlloc.Outbox;

internal sealed class WorkflowDispatchOutboxDispatcher(WorkflowNodeDispatcher dispatcher) : IOutboxTypeDispatcher
{
    public string TypeName => WorkflowDispatch.TypeName;

    public async ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<WorkflowDispatchMessage>(payload.Span)
            ?? throw new InvalidOperationException("Empty workflow dispatch payload.");

        await dispatcher.DispatchAsync(message, ct);
    }
}
```

```csharp
services.AddScoped<IOutboxTypeDispatcher, WorkflowDispatchOutboxDispatcher>();
services.AddOutbox(o =>
    {
        o.PollingInterval = TimeSpan.FromSeconds(5);
        o.BatchSize = 20;
        o.MaxAttempts = 8;
        o.RetryBaseDelay = TimeSpan.FromSeconds(2);
    })
    .WithOrm();
```

`.WithOrm()` registers `OrmOutboxStore`, whose only constructor parameter is an `IAsyncDbConnection` resolved from
DI. Nothing registers one for you, and the type arrives transitively from `AdoNet.Async.Adapters` rather than from
a package you added by name, so here it is in full — same database `AddWorkflowOrm` was given:

```csharp
using System.Data.Async;            // IAsyncDbConnection
using System.Data.Async.Adapters;   // the AsAsync() extension
using Npgsql;

services.AddScoped<IAsyncDbConnection>(sp =>
{
    var connection = new NpgsqlConnection(configuration.GetConnectionString("workflow"));
    connection.Open();
    return connection.AsAsync();
});
```

`OutboxWorkerService` opens a scope per batch, so a scoped dispatcher and a scoped connection are both fine — each
batch gets its own connection and disposes it with the scope.

Two things about the retry budget, because they decide what a failure costs. The dispatcher deliberately does
**not** throw for a node-level failure — a turn that failed, an unresolvable agent name, an outcome outside the
node's declared set — it records the run as `Failed` and returns, so the outbox has nothing to retry and you never
pay for the same losing agent turn `MaxAttempts` times. What does propagate, and therefore does get retried, is an
unexpected exception out of `ISubagentRunner` and a `WorkflowConcurrencyException` from the store: both are
transient by nature. And `MaxAttempts` is what eventually dead-letters a message that never succeeds — which is
what leaves a run stranded, and why step 5 exists.

## 5. The stranded-run sweep

```csharp
services.AddSingleton<WorkflowRunReconciler>();
services.AddHostedService<WorkflowSweepService>();
```

```csharp
internal sealed class WorkflowSweepService(WorkflowRunReconciler reconciler) : BackgroundService
{
    private static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var terminated = await reconciler.SweepAsync(StrandedAfter, ct);
            // log terminated when non-zero
        }
    }
}
```

`SweepAsync` terminates; it never advances. Every run it touches is failed, seq-guarded against the exact row the
query saw, so a run a concurrent dispatch completes in between is left alone. Runs in `Awaiting` are excluded
outright — a gate has nothing in flight by design and may legitimately sit for days.

**Choosing the threshold is the whole risk.** It must comfortably exceed both the longest a healthy node's agent
turn runs — minutes, routinely — and the outbox's own retry-and-backoff window, because `updated_at` does not
advance while a message is still retrying. Sized only against turn length, this sweep terminates runs whose next
delivery attempt would have succeeded. With `MaxAttempts = 8` and `RetryBaseDelay = 2s` the backoff alone reaches
roughly four minutes; 30 minutes leaves room for both. `SweepAsync` rejects a zero or negative threshold rather
than let a config binding that resolved to a default take the fleet down.

The reason it records against a run is deliberately hedged: from the run row alone, a dead-lettered dispatch and a
dispatch that was never enqueued look identical. Check the outbox's dead-letter rows for that run before concluding
which happened.

## 6. Getting process files into the table

A process is only runnable once its YAML is in `process_definition`. That is what `ProcessDefinitionSync` does, and
where the YAML comes from is yours to decide — Thalos declares `IProcessDefinitionSource` and never reaches for a
filesystem or a git remote itself.

```csharp
internal sealed class DirectoryProcessSource(string root) : IProcessDefinitionSource
{
    public async ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct)
    {
        var documents = new List<ProcessDocument>();
        foreach (var path in Directory.EnumerateFiles(root, "*.process.yaml", SearchOption.AllDirectories))
        {
            documents.Add(new ProcessDocument(path, await File.ReadAllTextAsync(path, ct)));
        }

        return documents;
    }
}
```

```csharp
services.AddSingleton<IProcessDefinitionSource>(_ => new DirectoryProcessSource(processRoot));
services.AddSingleton<ProcessDefinitionSync>();
```

Call `SyncAsync` at startup, before anything starts a run, and again whenever your source changes:

```csharp
var synced = await provider.GetRequiredService<ProcessDefinitionSync>().SyncAsync(ct);
if (synced.IsFailure)
{
    logger.LogError("Process sync reported: {Error}", synced.Error);
}
```

`SyncAsync` loads, validates against the reference resolver, and only then activates. A document that fails either
step is reported and left alone — the version already active keeps running. Two documents in one batch declaring
the same process name fail the **whole** batch before anything is activated, because otherwise which one wins would
depend on listing order. A stored `(process, version)` is immutable: re-syncing the same version with different
content is refused, so editing a process file means bumping its `version`.

## 7. Starting a run

```csharp
var runId = await store.StartAsync(
    process: "pipeline",
    version: await definitions.GetActiveVersionAsync("pipeline", ct) ?? throw new InvalidOperationException("pipeline is not synced"),
    correlationKey: $"issue-42:attempt-{Guid.NewGuid()}",
    startNode: "implement",
    initialVariables: new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["issue"] = "gh-42",
        ["branch"] = "fix/null-guard",
    },
    ct);
```

That is the whole start. `StartAsync` writes the run, seeds its `Entered` event, and enqueues the start node's own
dispatch — all in one transaction, so a run never exists without the work behind its first node already scheduled.
You do not construct a `WorkflowDispatchMessage` yourself; step 4's consumer picks it up on the next poll.

`initialVariables` is the run's opening `Variables` bag: the work item the first node is meant to act on. Pass
`null` for a run that starts with nothing — the bag is then empty, never null. It is seeded only on the path that
actually starts a run, so a call whose `correlationKey` an earlier run already used starts nothing and seeds
nothing.

## 8. Passing work from one node to the next

Every task node's instruction text carries the run's accumulated variables, and a node with declared `outcomes`
reports its own by passing a `variables` object on the same `workflow__report_outcome` call it reports its outcome
on:

```json
{ "outcome": "implemented", "variables": { "changed_files": "src/Guard.cs", "summary": "added a null guard" } }
```

One tool, one read path. A node with no declared `outcomes` is offered no outcome tool at all, so it can be told
variables but has no way to report any — correct for a node that declares nothing it produces.

The values are merged into `WorkflowRun.Variables` by the same transactional merge `ResumeAsync` uses, later
writes winning on key collision, and a call carrying no `variables` leaves the bag exactly as it was rather than
clearing it.

**Variables reaching a later node are framed as untrusted, and you should treat them that way too.** They arrive
inside a `<workflow-variables note="...">` block whose note tells the reading agent they were written by other
agents and are information, not instructions. This is not decoration: without it, an implementing agent can write
instructions into a variable and steer the reviewer that reads them back, which defeats the point of having a
separate reviewer. Values and keys cannot close or forge that block — the tag is escaped in both — and they are
flattened to one line each.

A `variables` argument that is present but is not a JSON object makes the dispatcher discard **that call**,
outcome included. That is deliberate: the alternative is accepting the outcome while silently dropping what the
node reported alongside it. The node then fails with its usual "completed without calling ... to report an
outcome" *only when that was the only matching call* — a turn that also made a well-formed call still resolves on
the well-formed one, because the rejection is per call, not per turn. An explicit JSON `null` is not that case at
all: it reads as "no variables", the same as omitting the argument.

Variables reaching a later node are shortened to fit, and the shortening is stated in the block. See the limits
section below for which cap applies where.

`version` is pinned at start and never re-resolved. A run parked at a gate for a week comes back to the graph it
started on even if newer versions activated meanwhile; activating a new version only changes what *new* runs start
on.

**`correlationKey` is unique across every process and for all time.** The index behind it carries no process column
and no status predicate, so a key is never released — not when the run succeeds, not a month later. A second
`StartAsync` with a key any earlier run used returns *that* run's id, whatever process it belonged to and whatever
state it is in, and starts nothing. Mint keys that include the attempt, not a business identity that recurs.

## 9. Resuming an approval gate

A run that reaches a node with `await:` parks at `Awaiting` with the signal name recorded. Nothing advances it
until something calls:

```csharp
var resumed = await store.ResumeAsync(runId, signal: "human_approval", payload: "approved", ct);
```

Wire that to whatever approves — an HTTP endpoint, a chat command, a webhook. The payload lands in the run's
variables under the literal key `"payload"`. Every failure — run not found, signal mismatch, definition
unresolvable, optimistic-concurrency loss — comes back as a `Result` failure, not an exception.

## Limits worth knowing before you author a process

- **The variable block put in front of a node has three caps, and the value cap is the one that bites.** The
  run's own bag is never capped or trimmed; only what one node is *told* is. Any single value over 512 characters
  is cut and marked, and that happens on the very first lap that writes an oversized value — a `diff` is over the
  cap essentially always, so treat a `diff` variable as something the next node sees the beginning of, not the
  whole of. Keys are capped at 128 the same way. The 4000-character *block* cap is separate and only bites once
  enough **distinct** keys accumulate: a loop whose laps overwrite the same few keys keeps the key count flat and
  never drops an entry, while still shortening every oversized value on every lap. Nothing is rejected and no turn
  is skipped.
- **A list or a nested object is shortened by dropping whole members, never by cutting mid-token**, so what
  reaches the next node is still parseable JSON with the loss stated after it. That applies to the shapes the
  engine itself produces; a host that seeds `StartAsync` with some other structured type gets a character cut.
- **Everything the engine says inside the block is a `workflow-variables-…` element**, which is the same tag
  family keys and values are escaped against — so a variable cannot claim the engine withheld or shortened
  something it did not. Dropped entries are also logged at `Warning` with the omitted key names, because the
  notice inside the block is visible to the reading agent and to nobody else. The log gets the same escaped,
  bounded key list the block gets, from the same renderer: key names are agent-authored, and a log is a sink that
  needs every protection the prompt does.
- **The variable key space is bounded, and that is what makes the omitted-key list trustworthy.** A run's bag
  holds at most **16 distinct keys** and one node turn may contribute at most **8** variables. Exceeding either
  **fails the node**, with an error naming the cap — a rejected report is a contract you can see in the run's
  error and event log, where a silently trimmed one is indistinguishable from a node that chose to report less.
  Overwriting a key the run already holds is always allowed and never counts against the total, so a capped loop
  can overwrite the same few keys lap after lap for as long as it runs. `StartAsync` throws `ArgumentException`
  for a seed over the same cap.

  The caps are not arbitrary and not a size optimisation. Because the number of keys that can exist is bounded,
  the omission notice is sized to name **every** key it leaves out — so a node cannot flood the bag with
  decoy names to push `task_brief` out of the list and hide that a brief ever existed. Widening the list instead
  would only move the threshold at which that works.
- **`models:`, `lenses:` and `quorum:` are parsed and ignored.** A node declaring `models: [sonnet, opus]` runs
  once, against whatever single model the resolved agent is configured with, silently. See
  [`release.md`](release.md#process-file-keys-that-are-parsed-but-not-yet-honoured).
- **There is no unattended zero-cost relay.** The validator requires each node to be exactly one of task, gate or
  terminal, so a loop back to a capped node must pass through one of two things, and neither is free. Another
  **task** node costs one more paid agent turn per lap — budget that loop at two turns, not one. A **gate** costs
  no turn at all: `gate: { await: sig, next: work }` is a legal, validating pass-through that the dispatcher parks
  at `Awaiting` without running anything, and `ResumeAsync` then resolves its `next` edge back into the capped
  node. But it costs a lap of blocking on an external signal — nothing advances that run until something calls
  `ResumeAsync`. Pick which cost you want; you cannot have neither.
- **`maxVisits` is the only spend bound the engine has.** It is enforced on entry to the capped node and the
  validator now rejects an `onExceeded` that can route back into it, but nothing else caps what a run costs.
