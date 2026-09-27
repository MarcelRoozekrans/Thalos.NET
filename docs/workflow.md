# Wiring the workflow engine

From `dotnet add package` to a run that actually moves. `Thalos.NET.Workflow` is a graph model, a loader, a
validator, a pure interpreter and a node dispatcher; `Thalos.NET.Workflow.Orm` persists runs on PostgreSQL with a
transactional outbox. Neither package hosts anything. A run only advances once a host has assembled seven pieces,
and `AddWorkflowOrm` registers two of them — this page is the other five.

## What you are assembling

| Piece | Registered by `AddWorkflowOrm`? | What it does |
| --- | --- | --- |
| `IWorkflowStore` and `IWorkflowRunHistory` (one `OrmWorkflowStore`) | yes | Writes the run, the event log and the next dispatch in one transaction; reads the event log back, with each completed node's token usage |
| `IProcessDefinitionStore` (`OrmProcessDefinitionStore`, cached) | yes | The one answer to "what is this process, at this version" |
| `IWorkflowReferenceResolver` | **no** | Turns an `agent:`/`skill:`/`action:` name in the YAML into something that exists |
| `WorkflowNodeDispatcher` | **no** | Runs one node's agent turn and persists the transition |
| An outbox consumer for `WorkflowDispatch.TypeName` | **no** | Takes a queued message off the table and calls `DispatchAsync` |
| `WorkflowRunReconciler` on a timer | **no** | Terminates runs nothing is advancing any more |
| `IProcessDefinitionSource` + `ProcessDefinitionSync` | **no** | Gets your `.process.yaml` files into the table |
| `IWorkflowHostAction`s, only if a process uses `action:` | **no** | The host code an action node runs instead of an agent turn (§10) |

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

`AddWorkflowOrm` registers `IWorkflowStore` and `IWorkflowRunHistory` (one `OrmWorkflowStore` singleton behind both), `IProcessDefinitionStore` (an `OrmProcessDefinitionStore` behind
`CachingProcessDefinitionStore`), the options object, and — while `WorkflowOrmOptions.EnsureSchemaOnStartup` is
left on, which it is by default — a hosted service that applies the outbox and workflow migrations before the host
accepts work. Read [`release.md`](release.md#schema-migrations-and-rolling-deploys) before leaving that on across a
rolling deploy: migration 1004 is not backward compatible with pre-1004 code.

## 2. The reference resolver

`WorkflowReferenceResolver` resolves `agent:` over `IAgentCatalog` and `skill:` over `ISkillStore`, both of which
`AddThalos` already provides, and `action:` over the `IWorkflowHostAction`s it is given (§10). It is not registered
for you, because a host that resolves agent names some other way should be able to say so:

```csharp
services.AddSingleton<IWorkflowReferenceResolver, WorkflowReferenceResolver>();
```

Its `hostActions` parameter is required; the container fills it with every registered `IWorkflowHostAction`, or an
empty sequence when there are none. Agent names are matched case-insensitively against `AgentDefinition.Name`;
action names are matched ordinally. A skill whose file has disappeared does not count as existing.

## 3. The node dispatcher

```csharp
services.AddSingleton(sp => new WorkflowNodeDispatcher(
    sp.GetRequiredService<IWorkflowStore>(),
    sp.GetRequiredService<ISubagentRunner>(),
    sp.GetRequiredService<IWorkflowReferenceResolver>(),
    sp.GetRequiredService<IProcessDefinitionStore>(),
    sp.GetRequiredService<ISkillStore>(),
    resolveCaller: run => new WorkflowCaller(run),
    gates: sp.GetServices<IWorkflowDispatchGate>(),
    hostActions: sp.GetServices<IWorkflowHostAction>()));
```

`gates` is required — pass `[]` if you host none. See §4 for what it is and when it runs. `hostActions` is required
too — pass `[]` if no process uses `action:`. Give it the same actions the resolver gets, or a process can validate
and then fail at dispatch; §10 has the details.

`ISkillStore` is what a pinned run's task node loads its exact skill body through
(`ISkillStore.GetVersionAsync`) — `AddThalos`'s `UseSkills` already registers it, the same instance
`IWorkflowReferenceResolver` resolves `skill:` names against above. A run started with no `RunManifest` never
reads from it at all.

The `resolveCaller` argument is the one only you can supply: the `ISecurityContext` each run executes as. The
dispatcher never inspects what comes back from it — it forwards the value into `SubagentRunRequest.Caller`, and
Thalos's tool authorization does the rest. A host with no per-run identity can return one fixed service principal; a
host that runs workflows on behalf of people should return theirs, because that is what decides which tools the
node's agent is allowed to call.

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

**Dispatch gates.** `WorkflowNodeDispatcher`'s `gates` argument (§3) is a list of `IWorkflowDispatchGate` — a
host-supplied check run immediately before a task node's agent turn, and only before a task node's: a gate node,
one with `await:` (an unrelated use of the word "gate", inherited from the process YAML), a `terminal:` node and an
`action:` node are never gated, because none of them spends a turn. The motivating case is a run's own tool servers: a gate can start
them, or restart them after a host crash, and refuse the turn outright when they never come up, rather than
dispatching an agent into a turn that would fail on its first tool call. A gate that refuses returns a failed
`Result`; the dispatcher fails the run with that message, the same way a rejected outcome does, and calls no
further gate. Wire none with `gates: []` — a required argument, not an optional one, so a host cannot forget it by
omission. A run can sit inside a gate for as long as its own wait allows — up to ten minutes for the tool-server
case above — and that whole time counts against the reconciler's `olderThan` budget (§5): `updated_at` does not
move while a gate is running any more than it moves during the agent turn itself, so a gate's own wait has to fit
comfortably inside `StrandedAfter`, the same constraint the turn length already has to satisfy.

**A gate's own `OperationCanceledException` means `ct` was cancelled, and nothing else.** Both
`OutboxWorkerService` (ZeroAlloc.Outbox) and Daedalus's own outbox loop catch a dispatch failure
`when (ex is not OperationCanceledException)` — they treat cancellation as the loop's own shutdown signal, never
as "this message failed, retry it" — so an `OperationCanceledException` a gate throws for any other reason, for
example an HTTP client's own request timeout surfacing as `TaskCanceledException`, escapes both loops uncaught and
can stop the worker outright, not merely get this one message redelivered. A gate's own timeout must return a
failed `Result`, never throw. Fixing the host loops themselves is out of scope here: Daedalus's is fixed in task
B9, and ZeroAlloc.Outbox's is filed upstream as ZeroAlloc-Net/ZeroAlloc.Outbox#204.

Two things about the retry budget, because they decide what a failure costs. The dispatcher deliberately does
**not** throw for a node-level failure — a turn that failed, an unresolvable agent name, an outcome outside the
node's declared set, a gate that refused, an unregistered or failing host action — it records the run as `Failed`
and returns, so the outbox has nothing to retry and you never pay for the same losing agent turn `MaxAttempts`
times. What does propagate, and therefore does get retried, is an unexpected exception out of `ISubagentRunner`, a
gate's or a host action's own non-cancellation exception, and a `WorkflowConcurrencyException` from the store: all
of them are transient by nature and are caught and retried by the outbox exactly alike. An
`OperationCanceledException` from a gate or a host action is the one exception to that: see above. And
`MaxAttempts` is what eventually dead-letters a message that never succeeds — which is what leaves a run stranded,
and why step 5 exists.

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
    new WorkflowStartRequest
    {
        Process = "pipeline",
        Version = await definitions.GetActiveVersionAsync("pipeline", ct) ?? throw new InvalidOperationException("pipeline is not synced"),
        CorrelationKey = $"issue-42:attempt-{Guid.NewGuid()}",
        StartNode = "implement",
        InitialVariables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["issue"] = "gh-42",
            ["branch"] = "fix/null-guard",
        },
        // Every start names its starter. A caller with a human behind the request passes that human's identity;
        // a host with no human behind it — a timer, a webhook, a test harness — passes its own system principal.
        StartedBy = new RunPrincipal(currentUser.Id, currentUser.Roles),
    },
    ct);
```

That is the whole start. `StartAsync` writes the run, seeds its `Entered` event, and enqueues the start node's own
dispatch — all in one transaction, so a run never exists without the work behind its first node already scheduled.
You do not construct a `WorkflowDispatchMessage` yourself; step 4's consumer picks it up on the next poll.

`InitialVariables` is the run's opening `Variables` bag: the work item the first node is meant to act on. Pass
`null` for a run that starts with nothing — the bag is then empty, never null. It is seeded only on the path that
actually starts a run, so a call whose `CorrelationKey` an earlier run already used starts nothing and seeds
nothing.

`StartedBy` is required and non-null: every run records who started it, written once and never updated. `RunId`
is optional — leave it `null` and the store mints one, or supply your own so you can prepare a resource keyed by
the run's id, such as a git worktree, before the first node is ever dispatched.

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
var resumed = await store.ResumeAsync(
    runId,
    new WorkflowResumeRequest
    {
        Signal = "human_approval",
        Payload = "approved",
        // Every resume names its approver: a human gate exists to record who cleared it. A host with no human
        // behind the resume passes its own system principal, the same as WorkflowStartRequest.StartedBy.
        ResumedBy = new RunPrincipal(approvingUser.Id, approvingUser.Roles),
    },
    ct);
```

Wire that to whatever approves — an HTTP endpoint, a chat command, a webhook. The payload lands in the run's
variables under the literal key `"payload"`. Every failure — run not found, signal mismatch, definition
unresolvable, optimistic-concurrency loss — comes back as a `Result` failure, not an exception. `ResumedBy` is
required and non-null; a missing approver throws rather than returning one of these failures, because it is a
programming error, not something a caller can legitimately trigger.

A successful resume also records itself onto `WorkflowRun.LastResume`: who resumed the run, when, and which
signal they satisfied. `LastResume` is `null` for a run never resumed, and a refused resume — a signal mismatch,
an unresolvable definition, a lost concurrency race — leaves it exactly as it was; nothing is recorded until the
gate's own transition has already succeeded.

## 10. Host action nodes

A node can run a piece of host code instead of an agent turn. It names that code with `action:` and declares the
outcomes it can finish with, exactly like a task node that branches:

```yaml
nodes:
  publish:
    action: open-pull-request
    outcomes: [published, failed]
    branch: { published: done, failed: stop }
  done: { terminal: succeeded }
  stop: { terminal: failed }
```

The code behind the name is an `IWorkflowHostAction` whose `Name` is `open-pull-request`. Its `RunAsync` gets the
run and the node, and returns either a `HostActionResult` carrying one of the node's declared outcomes and the
variables to merge, or a failed `Result`, which fails the run with its message. It must not throw for a failure it
expected: an exception is left to propagate so the outbox retries the delivery, and a retry calls the action again
for the same node, so an action must be idempotent. An `OperationCanceledException` must mean only that the `ct`
it was given was cancelled: an action's own timeout returns a failed `Result`, because both outbox loops treat any
`OperationCanceledException` as their own shutdown. `IWorkflowDispatchGate` carries the same contract.

The validator treats a node as an action node whenever `action:` is set, and rejects:

- an action node that also names `agent:` or `skill:`, even one of the two:
  `node 'publish' is an action node and must not name 'agent' or 'skill'`;
- an action node with no `outcomes:`: `node 'publish' is an action node and must declare 'outcomes'`;
- a blank name, such as `action: ''`: `node 'publish' has a blank 'action'`;
- an action node that also sets `await:` or `terminal:`, through the rule that every node is exactly one of task,
  gate, terminal or action;
- with a resolver, an action nobody registered:
  `node 'publish' references unknown host action 'open-pull-request'`. `ProcessDefinitionSync` validates with the
  resolver, so such a process is never activated.

**Registering an action.** Register each action once and hand the same set to both the resolver and the
dispatcher. With the wiring in §2 and §3, registering it in the container is all it takes:

```csharp
services.AddSingleton<IWorkflowHostAction, OpenPullRequestAction>();   // Name => "open-pull-request"
```

Names are compared ordinally, so `Open-Pull-Request` is a different action. Two actions under one name, a blank
name or a null entry make the resolver's and the dispatcher's constructors throw `ArgumentException`, because which
of two same-named actions a node ran would otherwise depend on registration order. A host with its own
`IWorkflowReferenceResolver` must answer `HostActionExistsAsync` from the same set. The member is abstract, so a
resolver written before action nodes existed no longer compiles until it does.

**What the dispatcher does with an action node.** It runs the action instead of an agent turn. No dispatch gate
runs first (§4), and a run manifest is not consulted, because an action node has no agent or skill to pin. Then:

- An action name that is not registered fails the run:
  `node 'publish' references host action 'open-pull-request', which is not registered.` This can only happen when
  the resolver and the dispatcher were given different actions, or the action was removed after the process was
  synced.
- A failed `Result` fails the run with the action's message, prefixed with the node:
  `node 'publish': push failed; worktree kept at C:/w`.
- A successful `Result` whose `HostActionResult` or `Variables` is `null` fails the run instead of throwing, so
  the outbox never re-runs the action for it:
  `node 'publish': host action 'open-pull-request' returned a success with no variables; an action with none to report returns an empty dictionary.`
- The returned outcome must be one of the node's declared `outcomes`. That holds when the node leaves by `next`
  too, not only when it branches. An undeclared outcome is a defect in the action, and it fails the run with the
  same message an agent's undeclared outcome gets:
  `node 'publish' produced outcome 'merged' which is not one of its declared outcomes (published, failed)`.
- The returned variables merge into the run under the caps a task node's report has (see the limits below): at most
  8 per report and 16 distinct keys per run. Exceeding either fails the run.
- An exception propagates, and so does an `OperationCanceledException` when the dispatch `ct` was cancelled, so the
  outbox retries the delivery. Nothing is recorded, and the run stays at the node.

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
- **There is no unattended zero-cost relay a process can declare on its own.** The validator requires each node to
  be exactly one of task, gate, terminal or action. An action node runs whatever host code is registered under its
  name, so whether one is a free relay is the host's decision, not the process author's. Without one, a loop back to
  a capped node must pass through one of two things, and neither is free. Another
  **task** node costs one more paid agent turn per lap — budget that loop at two turns, not one. A **gate** costs
  no turn at all: `gate: { await: sig, next: work }` is a legal, validating pass-through that the dispatcher parks
  at `Awaiting` without running anything, and `ResumeAsync` then resolves its `next` edge back into the capped
  node. But it costs a lap of blocking on an external signal — nothing advances that run until something calls
  `ResumeAsync`. Pick which cost you want; you cannot have neither.
- **`maxVisits` is the only spend bound the engine has.** It is enforced on entry to the capped node and the
  validator now rejects an `onExceeded` that can route back into it, but nothing else caps what a run costs.
