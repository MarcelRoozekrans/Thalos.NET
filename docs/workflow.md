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
| `RunWorkspaceSweeper` on a timer, only if runs have workspaces | **no** | Removes a run's worktree, and stops its run-scoped MCP servers, once the run no longer needs them (§11) |

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

A host whose runs have workspaces also sets the run's `RunWorkspaceClaims.RunId` claim here; §11 shows how, and why
it must come from the run row only.

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
// The two budgets every outbox and sweep timing below is derived from. Take both from your own configuration.
TimeSpan gateWait = TimeSpan.FromMinutes(10);     // the longest any IWorkflowDispatchGate waits before it answers
TimeSpan turnDeadline = TimeSpan.FromMinutes(10); // SubagentBudget.Deadline, the agent turn's wall-clock cap

services.AddScoped<IOutboxTypeDispatcher, WorkflowDispatchOutboxDispatcher>();
services.AddOutbox(o =>
    {
        o.PollingInterval = TimeSpan.FromSeconds(5);
        o.BatchSize = 1;
        o.MaxAttempts = 8;
        o.RetryBaseDelay = TimeSpan.FromSeconds(2);
        o.LeaseDuration = gateWait + turnDeadline + TimeSpan.FromMinutes(5); // 25 minutes
    })
    .WithOrm(OutboxOrmDialect.Postgres);
```

Pass `OutboxOrmDialect.Postgres` (namespace `ZeroAlloc.Outbox.Orm`). Plain `.WithOrm()` selects the SQLite dialect,
whose batch claim has no `FOR UPDATE SKIP LOCKED`: against PostgreSQL, a worker then waits on rows another worker's
claim has locked instead of passing over them.

`LeaseDuration` is how long a worker's claim on a message lasts. ZeroAlloc.Outbox 3.0 renews it once per message,
just before `DispatchAsync`, never during it, so **`LeaseDuration` must be longer than the longest gate wait plus the
turn deadline**: a dispatch runs the dispatch gates first and the agent turn after them, and both count. (An
`action:` node's dispatch is its host action instead; if one of those can run longer, size against that.) A
dispatch that outlives its lease is claimed by another worker, which runs the same gate and the same agent turn
again. The 5-minute default is too short for any agent turn worth the name. The margin on top matters too: a lease
exactly as long as the worst case has none.

`BatchSize = 1` because every message here is a multi-minute agent turn. A worker dispatches its claimed batch one
message after another, and every message in the batch was leased at the claim, so with a batch of 20 the later
messages wait behind the earlier turns while their leases run out, and other replicas sit idle with nothing to
claim. One message per claim leaves the rest on the table for whichever worker is free.

`.WithOrm(...)` registers `OrmOutboxStore` over an `IAsyncDbConnection` resolved from DI. Nothing registers one for
you, and the type arrives transitively from `AdoNet.Async.Adapters` rather than from a package you added by name,
so here it is in full — same database `AddWorkflowOrm` was given:

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
dispatching an agent into a turn that would fail on its first tool call; §11 has that gate. A gate that refuses
returns a failed `Result`; the dispatcher fails the run with that message, the same way a rejected outcome does, and calls no
further gate. Wire none with `gates: []` — a required argument, not an optional one, so a host cannot forget it by
omission. A run can sit inside a gate for as long as its own wait allows — up to ten minutes for the tool-server
case above — and that whole time counts against the reconciler's `olderThan` budget (§5): `updated_at` does not
move while a gate is running any more than it moves during the agent turn itself, so a gate's own wait has to fit
comfortably inside `StrandedAfter`, the same constraint the turn length already has to satisfy, and inside the
outbox's `LeaseDuration` (§4) together with the turn.

**A gate's own `OperationCanceledException` means `ct` was cancelled, and nothing else.** ZeroAlloc.Outbox
3.0.1's `OutboxWorkerService` treats an `OperationCanceledException` as its own shutdown only while its stopping
token is cancelled; it then releases the message's lease without counting an attempt. Any other
`OperationCanceledException` — for example an HTTP client's own request timeout surfacing as
`TaskCanceledException` — is recorded as a failed attempt and retried with backoff, like any other exception. A
gate that lets its own timeout escape that way therefore refuses nothing: it silently uses up the `MaxAttempts`
that end in the message being dead-lettered, and the run sits stranded until the sweep in §5 terminates it, with
no gate message in its error. A gate's own timeout must return a failed `Result`, never throw. A host running its
own outbox loop instead of `OutboxWorkerService` should make the same distinction: treat an
`OperationCanceledException` as shutdown only when its own stopping token is cancelled.

Two things about the retry budget, because they decide what a failure costs. The dispatcher deliberately does
**not** throw for a node-level failure — a turn that failed, an unresolvable agent name, an outcome outside the
node's declared set, a gate that refused, an unregistered or failing host action — it records the run as `Failed`
and returns, so the outbox has nothing to retry and you never pay for the same losing agent turn `MaxAttempts`
times. What does propagate, and therefore does get retried, is an unexpected exception out of `ISubagentRunner`, a
gate's or a host action's own non-cancellation exception, and a `WorkflowConcurrencyException` from the store: all
of them are transient by nature and are caught and retried by the outbox exactly alike. A gate's or a host
action's own `OperationCanceledException` is retried the same way, which is exactly why it must not be thrown for a
timeout: see above. And
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
    // Longer than LeaseDuration (25) + gate wait (10) + turn deadline (10) + retry backoff (about 4), all from §4.
    private static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(60);

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

**Choosing the threshold is the whole risk.** `updated_at` moves only when a run transitions, so it stands still
through everything that can legitimately happen to one dispatch before the run moves on: the gate wait and the
agent turn, the lease left to run out when a worker dies mid-turn before another worker may claim the message, a
second gate wait and turn on that next attempt, and the outbox's retry backoff between failed attempts. So
**`StrandedAfter` must be longer than `LeaseDuration` + the longest gate wait + the turn deadline + the retry
backoff.** Sized against less, this sweep terminates runs whose next delivery attempt would have succeeded. With
`MaxAttempts = 8` and `RetryBaseDelay = 2s` the backoff alone reaches roughly four minutes, so §4's budgets come to
about 49 minutes, and 60 leaves a margin. Derive it from the same `gateWait` and `turnDeadline` as the lease, so
raising one raises the other. `SweepAsync` rejects a zero or negative threshold rather
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
var started = await store.StartAsync(
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
if (started.IsFailure)
{
    // A seed over the variable cap, or a caller-supplied RunId that another run already has. Nothing was written.
    logger.LogError("Could not start pipeline: {Error}", started.Error);
    return;
}

var runId = started.Value;
```

That is the whole start. `StartAsync` writes the run, seeds its `Entered` event, and enqueues the start node's own
dispatch — all in one transaction, so a run never exists without the work behind its first node already scheduled.
You do not construct a `WorkflowDispatchMessage` yourself; step 4's consumer picks it up on the next poll.

`StartAsync` returns a `Result<Guid>`, not a bare id. It fails, before writing anything, for the two mistakes a
caller can legitimately make: an `InitialVariables` bag over the 16-key cap (see the limits below), and a
caller-supplied `RunId` that a different run already has, which leaves that run untouched. A reused
`CorrelationKey` is not a failure: it succeeds with the earlier run's id.

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
it was given was cancelled: an action's own timeout returns a failed `Result`. The outbox records any other
`OperationCanceledException` as a failed attempt and retries it, so a timeout thrown that way silently uses up the
attempts that lead to dead-lettering instead of failing the run with a message (§4). `IWorkflowDispatchGate`
carries the same contract.

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

## 11. Run workspaces

A run can have a workspace of its own: a git worktree that its nodes read and write through the `workspace__*`
tools, and that its own copies of MCP servers such as Roslyn run against. `UseGitWorktreeWorkspaces`
(`Thalos.NET.Git`) registers the `IRunWorkspaceProvider`, and `UseRunWorkspaceTools` (`Thalos.NET`) adds the
tools; the README's "Run workspaces and git" covers both. This section covers what a host has to run around them.

**Creating one.** The host creates the workspace, then starts the run under the same id. Mint the run id yourself
and pass it as `WorkflowStartRequest.RunId` (§7):

```csharp
var runId = Guid.NewGuid();
var workspace = await workspaces.CreateAsync(
    new RunWorkspaceRequest(runId, Repository: repoPath, Remote: remoteUrl, DefaultBranch: "main",
        Branch: "fix/null-guard", Solution: "App.sln"),
    ct);
if (workspace.IsFailure)
{
    return;   // nothing to clean up, and no run to start
}

// then: await store.StartAsync(new WorkflowStartRequest { RunId = runId, ... }, ct);
```

**Tying a turn to its run.** The `workspace__*` tools and every `runScoped` MCP server pick the workspace from the
caller's `RunWorkspaceClaims.RunId` claim (`"thalos.run_id"`), and nothing else. Set it in `resolveCaller` (§3),
from the run row:

```csharp
sealed class WorkflowCaller(WorkflowRun run) : ISecurityContext
{
    public string Id => $"workflow:{run.Process}:{run.Id}";
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "workflow" };
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RunWorkspaceClaims.RunId] = run.Id.ToString(),
    };
}
```

Set `thalos.*` claims only from the run row and reviewed config. Never copy them from an inbound identity such as
a JWT, and never from a run variable: whoever controls `thalos.run_id` chooses which run's workspace and servers a
call reaches. A caller with no run claim is refused by the `workspace__*` tools and is served by the host-wide
MCP server, not a run's.

**Environment of stdio MCP servers.** From 0.13.0 a stdio server does not inherit the host's environment. It starts
from ModelContextProtocol's curated defaults (PATH, HOME/USERPROFILE, TEMP and the like), then the host variables
named in the entry's `passEnvironment` array, then its `env`, and for a run's copy `runScoped.env` last. A secret
such as `GITHUB_TOKEN` or `ANTHROPIC_API_KEY` reaches a server only if the entry lists it:

```jsonc
"github": { "command": "npx", "args": ["-y", "some-github-mcp"], "passEnvironment": ["GITHUB_TOKEN"] }
```

A run-scoped copy uses the host entry's `passEnvironment`. A server that relied on an inherited variable, such as
`DOTNET_ROOT` for `dnx`, must now list it.

**Run-scoped MCP servers.** An `.mcp.json` stdio entry with a `runScoped` object gets one private copy of the
server per run, started against that run's workspace. The host entry keeps serving callers with no run claim, and
its `command`, `timeout` and `shutdownTimeout` are reused for the run's copies:

```jsonc
{
  "mcpServers": {
    "roslyn": {
      "command": "dnx",
      "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"],
      "runScoped": {
        "args": ["RoslynCodeLens.Mcp", "--", "${run.workspace.solution}"],
        "env": { "RUN_ID": "${run.id}" },
        "cwd": "${run.workspace.root}",
        "readyTool": "list_solutions",
        "reload": "tool:rebuild_solution",
        "readyWaitTimeout": "00:10:00",
        "callTimeout": "00:02:00"
      }
    }
  }
}
```

- `${run.id}`, `${run.workspace.root}` and `${run.workspace.solution}` are replaced in `args`, `env` values and
  `cwd`; any other `${...}` is left as it is. `${run.workspace.solution}` fails the run's server start when the
  workspace was created with no `Solution`.
- `args` replaces the host entry's arguments; left out, the host entry's are used, also substituted. `env` is
  layered over the host entry's, a key set here winning. `cwd` defaults to `${run.workspace.root}`.
- `readyTool` is called with no arguments every two seconds after the server lists its tools, until a call does
  not return an error; only then is the server ready. Left out, the server is ready as soon as its tool list
  returns.
- `reload` decides what happens before the next call once files in the workspace have changed through the
  `workspace__*` tools: `"none"` (the default), `"tool:<name>"` to call that tool with no arguments, or
  `"restart"`. A reload re-evaluates the workspace's build files, and MSBuild runs code from them, so use one only
  while the run's writable extensions exclude `.csproj`, `.props`, `.targets` and the like.
- `readyWaitTimeout` bounds how long one call waits for a start or reload still in progress; `callTimeout` bounds
  how long one call may run. Both are `hh:mm:ss` strings and default to two minutes.

**Waiting for them before a turn.** A run's servers start in the background when its workspace is created, and a
host restart loses them. `IRunToolServerReadiness.WaitAllReadyAsync`, registered by the first `runScoped` entry,
starts whatever is not running — after a restart it finds the workspace through the provider and starts the
servers then — and waits until every one is ready. Call it from a dispatch gate (§4), so a turn is refused, not
wasted, when a server never comes up:

```csharp
internal sealed class RunToolServersGate(IRunToolServerReadiness servers) : IWorkflowDispatchGate
{
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(10);   // the gateWait budget in §4

    public async ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
    {
        var ready = await servers.WaitAllReadyAsync(run.Id, Wait, ct);
        return ready.IsSuccess ? Result.Success() : Result.Failure(ready.Error.Message);
    }
}
```

```csharp
services.AddSingleton<IWorkflowDispatchGate, RunToolServersGate>();
```

Register this gate only when at least one `runScoped` entry exists: `IRunToolServerReadiness` is registered by the
first such entry, so without one the gate cannot be resolved. `WaitAllReadyAsync` fails naming the server when one
is not ready within the timeout, and fails for a run that has no recorded workspace, so a host whose runs do not all
have one skips the wait for those. Its own timeout is a failed result, and it throws `OperationCanceledException`
only when `ct` is cancelled, which is what the gate contract in §4 asks for.

**Removing them.** Nothing removes a workspace on its own. `RunWorkspaceSweeper` (`Thalos.NET.Workflow`) does,
and, like the reconciler in §5, it has no timer: host `SweepAsync` on a schedule, or worktrees and each run's
server processes stay until the host shuts down.

```csharp
services.AddSingleton<RunWorkspaceSweeper>();
services.AddHostedService<RunWorkspaceSweepService>();
```

```csharp
internal sealed class RunWorkspaceSweepService(RunWorkspaceSweeper sweeper) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var removed = await sweeper.SweepAsync(ct);
                // log removed when non-zero
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // log and keep sweeping: an unhandled exception here would stop the host
            }
        }
    }
}
```

The sweeper decides from the run row, read just before each removal:

- **Kept:** `Running` and `Awaiting` runs, and `Failed` or `Cancelled` runs that were resumed at least once
  (`LastResume` is set). A run that failed after approval keeps its worktree for a human to look at; remove it
  with `IRunWorkspaceProvider.RemoveAsync` once they are done.
- **Removed:** `Succeeded` runs, `Failed` or `Cancelled` runs never resumed, and workspaces whose run row does not
  exist once they are older than `RunWorkspaceSweeper.OrphanGrace` (10 minutes), the window between creating a
  workspace and starting its run.

Removing a workspace stops the run's `runScoped` servers first. A removal that fails or is refused is logged and
retried by the next sweep, never thrown. `SweepAsync` returns how many it removed; it throws when `ct` is cancelled
and when listing the workspaces itself fails, which is why the service above catches and logs.

### Run sandboxes

`Thalos.NET.Sandbox`, `Thalos.NET.Sandbox.Docker` and `Thalos.NET.Sandbox.Host` (0.14.0, all `net10.0`-only) move a
run's workspace, build and Roslyn into a per-run container, so that code the agent wrote (MSBuild targets, tests,
analyzers) never executes on the host. `UseSandboxRunWorkspaces` (`Thalos.NET.Sandbox`) replaces the worktree
provider of `UseGitWorktreeWorkspaces`; `UseDockerSandboxRuntime` (`Thalos.NET.Sandbox.Docker`) supplies the
`ISandboxRuntime`. `Thalos.NET.Sandbox.Host` is the entry point of the image, see `samples/Thalos.Sample.SandboxHost`.

**Architecture.** The API talks to a loopback-published gateway container (nginx), which routes each request by
run to that run's own container. The container has no route out except an egress proxy (squid) that allows only
`api.nuget.org`, `*.nuget.org` and `globalcdn.nuget.org`, plus domains the host adds in
`DockerSandboxOptions`. Everything trusted lives on the host under `SandboxOptions.DataRoot`: the git mirror,
the sandbox records, the stored patches, and the publish worktrees (`<DataRoot>/publish`). The sandbox's own
volume is never read from the host.

**Requires a Linux container engine.** The sandbox, gateway and egress images are Linux-only. On Windows that means
Docker Desktop in Linux-container mode (WSL 2 or Hyper-V backend), not Windows-container mode. Against an engine that
runs Windows containers, `CreateAsync` fails with "run sandboxes need a Linux container engine; this engine runs
windows containers" before any network, container or pull call, and `GetAsync` and `ListAsync` log it and answer
null and empty.

```csharp
thalos
    .UseDockerSandboxRuntime()
    .UseSandboxRunWorkspaces(o =>
    {
        o.Image = "registry.example/thalos-sandbox@sha256:...";
        o.DataRoot = "/var/lib/app/sandboxes";             // absolute
        o.AllowedWriteExtensions = new HashSet<string> { ".cs", ".csproj", ".md" };   // null = any extension
        o.ProtectedPaths.Add("AGENT.md");                   // extra entries, on top of the shipped defaults
    });
```

**Invariants.** Each has a test that guards it.

- **S1.** The container's environment is the keys `SandboxSpec.Environment` lists (run id, bearer token, write
  extensions, protected paths, proxy variables) plus the image's own `ENV`; nothing from the API's environment.
- **S2.** The container reaches no host except through the egress proxy.
- **S3.** MSBuild, restore and the tests run only inside the container, never on the host.
- **S4.** The API never evaluates MSBuild, runs restore, or checks out a tree a sandboxed process touched. It
  applies a patch into its own clean worktree.
- **S5.** A change to a protected path fails publish. The publish-side check, in `GitPatchApplier`, is the control;
  the sandbox-side refusal is a convenience.
- **S6.** A grant with no extension list (`AllowedWriteExtensions` null, `"*"` on the wire) is only safe under a
  sandbox. Refusing it otherwise is the host's job: Thalos does not know what a host's grants are. Daedalus
  refuses an any-extension grant at boot unless `Thalos:Workflow:Sandbox:Enabled` is true.

**Lifecycle.** `CreateAsync` starts the container and imports the repository into it from a git bundle cut from the
trusted mirror, then the run is ready and its `workspace__*` and `sandbox__*` tools and its `runScoped` servers are
served by the container. At the approval gate the host parks the run, `IParkableRunWorkspaceProvider.ParkAsync`: the
sandbox exports its changes as a patch against the base commit, the patch is stored under `DataRoot`, and the
container is deleted, so a run waiting on a human holds no container. A sandbox that exited is started again for the
export, at most twice; one that no longer exists parks the run without a patch. On approval,
`IRunWorkspaceHandoff.CheckoutForPublishAsync` cuts a clean worktree at the run's base commit from the mirror and
applies the stored patch with `GitPatchApplier`, which treats it as adversarial: bounded size, protected paths, and
no symlinks or submodules. The host then commits and publishes from that worktree. Both calls are idempotent, and a
failed park keeps the sandbox and is retried by the next sweep.

**A park is final (ruling R39).** Once a run is parked, nothing unparks it or creates its sandbox again before
publish: its `workspace__*`, `sandbox__*` and remote `runScoped` tools all answer that the run has no sandbox. So in
sandbox mode, a process may not run an agent node after the run is parked: none after an await gate, and no reject
edge that loops back to an agent node. Such processes are not supported in sandbox mode yet; unparking or re-creating
a parked run's sandbox is future work.

**Bounds on a run's tool calls.** `RemoteRunToolOptions` bounds every call the host makes to a run's sandbox.
`CallTimeout` (default 30 minutes) bounds one call; keep it above the sandbox's own bounds on a `sandbox__test` call,
which can wait for a restore (10 minutes) and then copy the workspace and run the tests within
`SandboxToolOptions.TestTimeout` (15 minutes by default), so the sandbox, not the host, reports what timed out.
`MaxResultBytes` (default 4 MiB) caps each response read from the sandbox: a longer answer, which agent-controlled
code such as an analyzer can make arbitrarily large, ends the call with an `error:` result and drops the run's client.

**`passEnvironment` and `runScoped.remote`.** A sandboxed host has no local run-scoped servers. Registration fails
at boot with an `InvalidOperationException` naming every `runScoped` entry that is not `"remote": true`, because
the host would start it on the host against a `sandbox://` root. A remote entry keeps its schema and serves callers
with no run on the host, but a run's calls go to the server in that run's container, found through
`IRunToolEndpointResolver`. It must not set `args`, `env`, `cwd`, `readyTool`, `reload` or `readyWaitTimeout`;
`callTimeout` still bounds each call. The entry must be named `roslyn`: a run's calls go to the sandbox's
`mcp/{name}` route, and the sandbox host serves only `workspace`, `sandbox` and `roslyn`, so a remote entry by any
other name is refused at boot with an `InvalidOperationException`. `passEnvironment` is unaffected and applies only
to servers the host itself starts; it never reaches a container (S1).

```jsonc
"roslyn": {
  "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"],
  "runScoped": { "remote": true, "callTimeout": "00:02:00" }
}
```

**`ProtectedPathSet`.** Repository-relative paths a run may read but never write; the same type backs the
`workspace__*` tools, the sandbox host and the publish check. An entry ending in `/` protects that directory and
everything under it (`.github/` covers `.github/workflows/ci.yml`); any other entry protects exactly that file.
Matching is case-insensitive, `\` is read as `/`, empty and `.` segments are dropped, and every other segment is
compared without the trailing dots and spaces Windows drops from a name (`.github./x.yml` is under `.github/`). A
`..` segment, or one of only dots and spaces, throws `ArgumentException` in an entry, at registration; in a path
being checked, `IsProtected` returns true, failing closed. `SandboxOptions.DefaultProtectedPaths` ships the
reviewed defaults (ruling R38), which every sandboxed run gets: `.git/`, `.gitattributes`, `.gitmodules`, `.github/`,
`.gitlab-ci.yml`, `azure-pipelines.yml`, `.azure-pipelines/`, `.circleci/` and `Jenkinsfile`.
`SandboxOptions.ProtectedPaths` holds extra entries, added to the defaults and de-duplicated; the standing-instructions
file (`AGENT.md`) is the host's to add there. Entries may not contain `;`, which joins them on the wire.

**Residual risks.**

- Agent code and the sandbox host run as the same user inside the container. Anything that discloses the bearer
  token opens that run's own sandbox and no other: tokens are per run and S1 keeps every other secret out.
- A sandboxed process can deliberately mutate the workspace while a reviewer or the agent is working in it, and
  Thalos cannot tell that from a legitimate edit. The approval gate and the pull request review are the backstop,
  with S5 limiting what a patch may touch.
- `RepoConfigGuard` lists the git config before each git command that reads the worktree and refuses a tampered
  one, but a write between the check and the command it guards is a time-of-check race. The publish side treats the
  patch as adversarial regardless, and never runs git against the sandbox's tree.
- Inside the container, the isolation `HOME`/`XDG_CONFIG_HOME` directory of the sandbox host's git commands is
  agent-writable. Agent code can plant `git/attributes` or `git/ignore` there, which change how git reads the tree,
  but not an execution path: filters and drivers are config, and git's config stays isolated. The publish side runs
  git on the host, with its own isolation directory.

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
  can overwrite the same few keys lap after lap for as long as it runs. `StartAsync` returns a failed `Result`,
  before writing anything, for a seed over the same cap.

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
