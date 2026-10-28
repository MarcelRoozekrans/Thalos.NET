# Versioning and releases

Same design as [Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET) and AdoNet.Async.

- **No prereleases.** nuget.org only ever receives stable `X.Y.Z` versions, and only from the commit
  release-please tagged `vX.Y.Z`. `publish-nuget` refuses everything else.
- **GitVersion** (`GitVersion.yml`, dotnet local tool pinned in `.config/dotnet-tools.json`) derives every
  build's version from git history. `main` carries no label, so it derives plain stable numbers (`0.1.0`
  until the first tag, then the tag's version bumped per GitHubFlow); the commit tagged `vX.Y.Z` derives
  exactly `X.Y.Z`; other branches derive `X.Y.Z-<branch>.N`. `ci.yml`'s `pack-validate` job packs with
  `-p:Version=$PACKAGE_VERSION` on every push and rehearses the nuget.org push against a local feed —
  those packages never leave the runner.
- **release-please** (`.github/workflows/release-please.yml`, manifest mode:
  `release-please-config.json` + `.release-please-manifest.json`) proposes releases from conventional
  commits and cuts the tag. Runs on every push to `main`, keeping a release pull request open and
  current; manual dispatch is still accepted.
- **Conventional commits** are enforced on pull requests by the `commitlint` job (`.commitlintrc.yml`).
- **Publishing** is the `publish-nuget` job in `ci.yml`: Trusted Publishing (no stored API key), gated
  on the full matrix and `pack-validate`. release-please calls it once it has cut the tag, so merging
  the release PR publishes; a manual dispatch with `publish_to_nuget=true` does the same by hand.

## One-time setup

```bash
# 1. nuget.org → Account → Trusted Publishing: add a policy with repository owner MarcelRoozekrans,
#    repository Thalos.NET, workflow file ci.yml (no environment). Do this while logged in as the
#    account that owns / will own the Thalos.NET.* package ids.
# 2. The account name the policy belongs to, as a repository *variable* (it is a username, not a secret):
gh variable set NUGET_USER --repo MarcelRoozekrans/Thalos.NET --body "<nuget.org username>"
```

## Cutting a release

**Merge the release PR. That is the whole procedure.**

Every push to `main` runs release-please, which keeps a pull request titled `chore(main): release
X.Y.Z` open and up to date with the commits since the last tag. It proposes; it releases nothing.
Merging it is the single deliberate act that cuts a release:

1. Review the release PR — the changelog and the proposed version are its diff.
2. Merge it, like every PR here.
3. release-please creates the GitHub release and the `vX.Y.Z` tag, then calls `ci.yml` with
   `publish_to_nuget=true` **and the tag as the ref to check out** — so the gates, the version
   derivation and the pack all run on the tagged commit, not on whatever `main` points at by then.
   `build-test` on both operating systems and `pack-validate` run first; `publish-nuget` refuses to
   start until they are green, and refuses to push unless the commit is tagged `vX.Y.Z` and the
   version is not a prerelease.
4. Confirm the version is listed on nuget.org.

Nothing to dispatch. If you need to drive it by hand anyway — re-running after an infrastructure
flake, or publishing a tag whose automatic run failed — both workflows still accept a manual
dispatch and behave exactly as they did before:

```bash
gh workflow run release-please.yml --ref main                  # propose, or cut the tag
gh workflow run ci.yml --ref vX.Y.Z -f publish_to_nuget=true   # publish a tagged commit
```

> **Why the publish is a `workflow_call` and not a `push: tags` trigger on `ci.yml`.**
> The call runs in the same run that cut the release and checks out exactly that tag. release-please
> works with a token from the release bot GitHub App (the `RELEASE_BOT_CLIENT_ID` variable and the
> `RELEASE_BOT_PRIVATE_KEY` secret), so its tag push *does* start workflows: a `push: tags` trigger
> added to `ci.yml` would fire next to the call and publish the release twice. Do not add one.
>
> Before the App token, release-please ran on the default `GITHUB_TOKEN`, whose events start no
> workflow — so the release PR arrived with no checks and a tag trigger would never have fired. A
> PAT was rejected as a long-lived stored credential acting as a person; the App key is the one
> stored credential now, it never expires and acts as no person, and the nuget.org push is still
> Trusted Publishing with no stored key.

Pre-1.0 bump rules (`release-please-config.json`): `bump-minor-pre-major` keeps a `feat!:` /
`BREAKING CHANGE` at a minor bump (0.4.0 → 0.5.0) instead of jumping to 1.0.0, a `feat:` takes the
minor, and a `fix:` takes the patch. Once 1.0.0 is cut those become major/minor as usual.

`bump-patch-for-minor-pre-major` was removed on 2026-09-17. It had held a `feat:` to a patch bump,
which meant every deliberate minor — 0.1.0, 0.2.0, 0.3.0 and 0.5.0, in practice nearly every release
this project has cut — needed a hand-written empty `Release-As:` commit to override it. Forgetting
that commit did not fail; it proposed the wrong version. 0.5.0 would have shipped as 0.4.1.

A `Release-As: x.y.z` footer on an empty commit still overrides the derived version, and is still
the right tool for a genuinely chosen number — the first release, or an intentional jump:

```bash
git commit --allow-empty -m "chore: set the release version" -m "Release-As: 1.0.0"
```

0.2.0 ships eight packages (`Thalos.NET.Memory` and `Thalos.NET.Memory.RagNet` joined the six of 0.1.x);
`pack-validate` checks the package list and each package's TFMs (`Thalos.NET.Memory.RagNet` is
`net10.0`-only, the others ship `net8.0` + `net10.0`) and rehearses the push of all eight.

0.3.0 ships nine packages (`Thalos.NET.Skills` joined the eight of 0.2.x); pre-1.0 a `feat:` still bumps
the patch, so 0.3.0 used the same `Release-As: 0.3.0` empty commit as 0.2.0 did. `pack-validate` expects
the nine ids and `Thalos.NET.Skills` on both TFMs.

0.4.0 ships eleven packages (`Thalos.NET.Channels` and `Thalos.NET.Channels.Telegram` join the nine of 0.3.x, both
on `net8.0` and `net10.0`). `ci.yml`'s `pack-validate` job (the `expected` package list and both package-count
checks) is updated for the two new packages; both were also verified locally by running the job's exact validation
logic against a real pack of each (README.md, logo.png, both TFMs' dll/xml, no runtimeconfig.json, MIT licence
expression, repository metadata, non-default description — all present for both).

**The 0.4.0 changelog note for the `IChannelAdapter.DeliverAsync` re-key does not need a manual release-PR step.**
`IChannelAdapter.DeliverAsync` was re-keyed from `SessionId` to `ConversationId` in `2bc8431`
(`refactor(channels)!: key IChannelAdapter on the conversation, not the session`), whose footer reads `BREAKING:
…` rather than the conventional-commits `BREAKING CHANGE: …` keyword release-please's parser matches note text
against. The `!` already forces the correct minor bump pre-1.0 and a "⚠ BREAKING CHANGES" heading using the commit's
own subject line — that part was never at risk. What was at risk was the two-sentence rationale, which a
`BREAKING:`-only footer would not carry into the generated entry. Rather than leave that as a step for whoever cuts
the release PR to remember (which is exactly the gap 0.3.0 hit — see `806f612`, "chore(main): release 0.3.0", whose
extra `docs:` commit patched a thin generated entry by hand after the fact), an empty commit already sits on this
branch carrying a properly-keyworded `BREAKING CHANGE: …` footer with that same explanation. release-please scans the
whole commit range for a package, not just squash-merge headers, so it will pick this up the same way it would a
non-empty commit. The full explanation also lives in
[`README.md`](../README.md#breaking-change-ichanneladapterdeliverasync-now-takes-a-conversationid) for a human reading
the package itself, independent of what release-please renders.

The next release ships fifteen packages: `Thalos.NET.Workflow` and `Thalos.NET.Workflow.Orm` join the thirteen
already shipping as of 0.6.0 (`Thalos.NET.Git` and `Thalos.NET.Git.LibGit2Sharp`, released in 0.6.0, having
joined the eleven of 0.4.x). `Thalos.NET.Workflow` ships `net8.0` + `net10.0`; `Thalos.NET.Workflow.Orm` is
`net10.0`-only, same reason as `Thalos.NET.Memory.RagNet` — its own dependencies (`ZeroAlloc.ORM`,
`ZeroAlloc.Outbox.Orm`, `AdoNet.Async.Adapters`) ship `net10.0`-only builds. `ci.yml`'s `pack-validate` job — the
`expected` package list, the TFM-selection branch, and both package-count checks and their error-message text —
is updated for both new packages.

0.14.0 ships eighteen packages: `Thalos.NET.Sandbox`, `Thalos.NET.Sandbox.Docker` and `Thalos.NET.Sandbox.Host` join
the fifteen above, and all three are `net10.0`-only. `ci.yml`'s `pack-validate` job, the `expected` package list,
the `net10.0`-only branch of the TFM selection, and both package-count checks and their messages, now name the three.
The sandbox container image is not a package: a host builds it from `Thalos.NET.Sandbox.Host` (see
`samples/Thalos.Sample.SandboxHost`). Behaviour and invariants: [workflow.md, Run sandboxes](workflow.md#run-sandboxes).

**Behaviour changes in 0.14.0**, for hosts that do not use sandboxes too:

- `WorkspacePath` refuses a workspace root that is not a fully qualified host path, such as a sandboxed run's
  `sandbox://<id>`, with its generic refusal. Before, `Path.GetFullPath` quietly resolved such a root under the
  current directory.
- `ProtectedPathSet` entries that end in `/` protect that directory and everything under it, and paths are
  canonicalised before comparing: `\` is read as `/`, empty and `.` segments are dropped, and trailing dots and
  spaces are trimmed from each segment. An entry with a `..` segment, or a segment of only dots and spaces, throws
  `ArgumentException`; `UseRunWorkspaceTools` and `UseSandboxRunWorkspaces` now throw it at registration rather than
  on the first tool call. A path being checked with such a segment is reported protected.
- `AddLocalTools` throws `ArgumentException` when a remote run tool source or a remote `runScoped` MCP entry already
  has its name, and the remote registrations throw the same way when a local source has it: the tool catalog keeps
  the first source of a name, so a run could otherwise be served by the wrong one.
- `UseGitWorktreeWorkspaces` registers `IRunBaseFileReader` and `IRunWorkspaceHandoff` by casting the registered
  `IRunWorkspaceProvider`. A host that replaces the provider after calling it must give a provider that implements
  both, or replace those two registrations as well; otherwise resolving either throws `InvalidCastException`.
- `RunWorkspaceSweeper` now parks the workspace of every run that is not `Running` when the provider is an
  `IParkableRunWorkspaceProvider`, within its `ParkBudget`, before deciding what to remove. Its rotation lives in the
  instance, so register the sweeper as a singleton and call the same one on every tick.

0.14.1 is a patch for sandbox hosts. It ships the same eighteen packages.

**What a sandbox host must do in 0.14.1:** commit the publish worktree that `IRunWorkspaceHandoff.CheckoutForPublishAsync`
returns with `GitCommitRequest.CommitStagedIndex = true`, and leave `Paths` null on that commit. See
[workflow.md, Run sandboxes](workflow.md#run-sandboxes).

- **A sandboxed publish could differ from what the S5 check passed.** `GitPatchApplier` stages the run's patch with
  `git apply --index`, and that index is what its protected-path check passed. `GitCliRunWorkspaceGit.CommitAsync`
  always reset the index and restaged the worktree from disk with `git add -A`, which silently dropped a file the
  patch added that the worktree's `.gitignore` matches, on every platform, and a mode change under
  `core.fileMode=false`, the Windows default. The new `GitCommitRequest.CommitStagedIndex` commits the index as it
  stands: no reset, no add, `ExcludePaths` still unstaged, and `Paths` refused with a validation error. With nothing
  staged it makes no commit, the same as before. With the option off, the default, `CommitAsync` behaves exactly as
  in 0.14.0, which local-worktree hosts rely on.
- **`UseSandboxRunWorkspaces` registers `IRunWorkspaceGit`** (issue #250). In 0.14.0 only `UseGitWorktreeWorkspaces`
  did, so a host that registered only the sandbox provider had nothing to commit and push the publish worktree with.
  It now replaces `IRunWorkspaceGit` with a `GitCliRunWorkspaceGit` over the same `<DataRoot>/publish` options as the
  mirror, the patch applier and the publish worktrees, using the registered `IGitCredentialSource`. A host that
  registered its own `IRunWorkspaceGit` before calling `UseSandboxRunWorkspaces` must register it after the call
  instead, since the call replaces it. A host that registered its own only to work around #250 can now remove that
  registration.

0.16.0 ships the same eighteen packages.

- **`OrmWorkflowStore`'s constructor gains two optional trailing parameters**, `IEnumerable<IWorkflowRunObserver>?
  observers = null` and `ILogger<OrmWorkflowStore>? logger = null`, so the store can tell observers about each change
  it commits. See [workflow.md, Observing run changes](workflow.md#observing-run-changes). This is source-compatible:
  a call with two arguments still compiles. It is binary-breaking for an assembly compiled against 0.15.x that
  calls the constructor directly, which must be rebuilt. Hosts that use `AddWorkflowOrm` are unaffected; it passes
  the registered observers and the logger itself. 0.16.0 is a minor release before 1.0, which is where such a break
  belongs.

0.18.0 ships the same eighteen packages. `Thalos.NET.Sandbox.Host` is AOT-clean: it is built with `IsAotCompatible`
and the request delegate generator, so a sandbox entry point that calls `SandboxHost.CreateBuilder` publishes as
Native AOT with no trim or AOT warning. `tests/Thalos.NET.Tests.Sandbox.Host.Native` is that entry point, published
and run by `NativeAotSmokeTests` on Linux x64.

- **`SandboxHost.CreateBuilder` no longer carries `[RequiresUnreferencedCode]` or `[RequiresDynamicCode]`**, and
  neither do `UseRunWorkspaceTools` and `UseRunWorkspaceToolsAllowingAnyExtension`. A caller that suppressed the
  warnings can drop the suppression.
- **`ThalosBuilder.AddLocalTools<TTools>(sourceName, serializerOptions, params Func<TTools, Delegate>[] tools)`** is
  the reflection-free form of `AddLocalTools(sourceName, params Type[])`: each tool is a method group such as
  `t => t.ReadFile`, and arguments and results go through the given options, typically a source-generated
  `JsonSerializerContext`. The workspace and sandbox tools are registered this way now. `AddLocalTools(typeof ...)`
  is unchanged for JIT hosts.
- **The sandbox host fails fast on a tool source that cannot build its tools.** It builds every registered source's
  tools once as it starts, before the server listens, so a bad binder or a principal hidden in a parameter stops the
  start instead of failing each MCP request.

**Breaking change in 0.18.0:**

- **`SandboxHost.CreateBuilder` returns a slim builder** (`WebApplication.CreateSlimBuilder`): no HTTPS
  configuration, no hosting startup assemblies, console logging only. It keeps configuration and Kestrel over HTTP,
  and adds back the `regex` route constraint its MCP route needs. A host that relied on any of the others in the
  builder `CreateBuilder` returns must now add them itself.

**Breaking changes accumulated across the workflow-engine phase (2.2, Part A):**

- `ThalosAgentRuntime`'s public constructor gained a required `IOutcomeToolFactory outcomeTools` parameter
  ahead of the optional `logger` one, so a turn can be given the constrained-outcome tool schema a workflow
  node asks for. Any direct instantiation outside `AddThalos(...)` DI wiring needs the new argument.
- `OrmWorkflowStore`'s public constructor gained a required `IProcessDefinitionStore definitions` parameter —
  process definitions are now resolved from the store at run time instead of being supplied out of band.
- `IWorkflowReferenceResolver.AgentExistsAsync` was removed and collapsed into `ResolveAgentIdAsync`: one
  lookup answers both "does this agent exist" and "what id does it have," so `ProcessValidator` at load time
  and the node dispatcher at run time cannot drift out of agreement with each other.
- `IWorkflowStore` gained `FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct)`
  — source-breaking for any external `IWorkflowStore` implementer, needed by the reconciler to fail a run
  stranded by a dead-lettered dispatch message.
- `IProcessDefinitionStore.UpsertAndActivateAsync`'s return type changed to `ValueTask<Result>`, so a
  same-version re-sync whose content differs from what is stored comes back as a typed failure instead of a
  silent overwrite — see the behaviour-change note below.

**Migration 1004 must be deployed together with the code that writes `content_hash`.** This is the operationally
sharpest item in the phase: applying 1004 ahead of the code rollout breaks process sync with `23502` on every
instance still running pre-1004 code. Full mechanism and rollout guidance: [Schema migrations and rolling
deploys](#schema-migrations-and-rolling-deploys) below.

0.19.0 ships the same eighteen packages. Every packable package except `Thalos.NET.Testing` is now built with
`IsAotCompatible`, so the trim and AOT analyzers guard each of them on `net8.0` and `net10.0`. The native probe
under `tests/` publishes a host that references every packable project except `Thalos.NET.Anthropic` and
`Thalos.NET.Testing` as Native AOT, and CI compares its output with a JIT run.

**Breaking changes in 0.19.0:**

- **A run variable must be JSON-shaped.** Variables are now checked where they enter: the `NodeResult`
  constructor, `HostActionResult`, `WorkflowStartRequest.InitialVariables` and `WorkflowRunStartOptions.Variables`.
  The store and the prompt block write them through the new `WorkflowVariableJson`, with no `JsonSerializer`
  call, so they work under trimming and Native AOT. The stored bytes and the rendered block are unchanged, and a
  run's in-memory and read-back values keep their CLR types.

  A value must be a string, a number, a bool, null, a `JsonElement`, a `JsonNode`, or a list or string-keyed
  dictionary of those. A `byte[]` counts as a string and is written as Base64. NaN and the infinities are refused,
  and a value may nest at most 63 levels. Anything else, such as a `Guid`, a `DateTime`, an enum or a record,
  now throws `ArgumentException` where it enters, with this message:

  ```text
  Workflow variable '<path>' has type '<full type name>', which is not JSON-shaped. Allowed: string, number,
  bool, null, JsonElement, JsonNode, and lists or dictionaries of those.
  ```

  Before 0.19.0 such a value was serialized by reflection. To migrate, convert it before passing it: `Guid.ToString()`,
  `DateTime.ToString("O")`, an enum's name, and for a record a `JsonElement` from
  `JsonSerializer.SerializeToElement(value, yourContext.YourRecord)`. `WorkflowVariableJson.Validate` checks a bag
  with the same rule if you want to test one before a run starts. A list must be re-enumerable, because the
  check walks it more than once.
- **`Thalos.NET.Sentinel` moves to AI.Sentinel 3.2.1 and takes its 3.x defaults.** PII and credential detectors
  redact by default: an email address or an API key in a prompt reaches the model as a placeholder and the turn
  succeeds, instead of taking the severity action. The placeholders in tool-call arguments are restored before the
  tool runs. Set `RedactByDefault = false` in the `UseAISentinel` callback to restore the severity action.
  3.0 also widened `PiiLeakageDetector` to every email address at Medium severity. The memory scanner cannot
  redact, so with `OnMedium = Quarantine` any recalled memory that contains an email address is now quarantined,
  whatever `RedactByDefault` says. The scanner also honours a detector's own `DetectorConfiguration.Action` and
  quarantines on any quarantining detection, not only the most severe one. Host code that uses AI.Sentinel types
  directly meets the AI.Sentinel 3.0 breaking changes, for example `DetectorConfiguration.Enabled` is `bool?` and
  `SentinelContext` loses its `LlmId` argument. The Thalos public API is unchanged.

**Added in 0.19.0:**

- **`LocalToolSources`** in `Thalos.NET`: `CreateOptions(resolver)` builds the `JsonSerializerOptions` a typed tool
  source needs from a source-generated context, and `Create<TTools>` builds an `IToolSource` from method groups.
- **`AddRemoteRunTools<TTools>`** in `Thalos.NET.Sandbox` registers a remote run-tool source without reflection, the
  way `AddLocalTools<TTools>` does for a local one. The `Type` overload keeps its attributes.
- **`AddGitActionTools(sourceName)`** in `Thalos.NET.Git` registers `GitActionTools` through the typed path.
- **`RunWorkspaceToolOptions.SerializerOptions` and `SandboxToolOptions.SerializerOptions`** expose the options the
  workspace and sandbox tools use, for a host that registers them itself.
- **`WorkflowDispatchMessage.JsonTypeInfo`** is the generated metadata for the outbox payload. A host that reads or
  writes the payload should use it instead of a reflection-based `JsonSerializer` call.

**Changed in 0.19.0:**

- **`UseMemory`, `UseSkills` and `UseSandboxRunWorkspaces` no longer carry `[RequiresUnreferencedCode]` or
  `[RequiresDynamicCode]`**, and neither do the `MemoryToolSource` and `SkillToolSource` constructors or the
  `UseMemory` and `UseSkills` overloads that take `IConfiguration`. A caller that suppressed the warnings can drop the
  suppression. `UseSandboxRunWorkspaces` registers its sources through `AddRemoteRunTools<TTools>`.
- **The builder methods that register a generic implementation annotate the type parameter** with
  `DynamicallyAccessedMembers(PublicConstructors)`, such as `UseMemoryStore<TStore>` and `UseSkillIndex<TIndex>`. A
  caller that passes its own open generic parameter needs the same annotation.
- **`Thalos.NET.Workflow.Yaml.ProcessYamlContext` is public only as generated plumbing.** `ProcessLoader` reads
  process files through this YamlDotNet static context instead of reflection. It is hidden from IntelliSense and is
  not a supported API; do not reference it. It goes back to `internal` once upstream YamlDotNet respects the declared
  accessibility, tracked as Thalos #397 and [aaubry/YamlDotNet#1152](https://github.com/aaubry/YamlDotNet/issues/1152).
- **A process node written as `name:` with nothing after it now reports an error** that names the node, instead of
  `ProcessLoader.Load` throwing `NullReferenceException`. A deployer should expect `SyncAsync` to report it for such a
  file, as for the shapes under [Process files that used to sync and now report an error](#process-files-that-used-to-sync-and-now-report-an-error).

0.20.0 ships nineteen packages: `Thalos.NET.Ollama` joins the eighteen, on `net8.0` and `net10.0`. `ci.yml`'s
`pack-validate` job lists it, and both package-count checks expect nineteen. `Thalos.NET.Anthropic` no longer depends
on the Anthropic SDK: it talks to the Messages API through `ZeroAlloc.Rest.Anthropic`, an AOT-clean client, so the
native probe now references every packable project except `Thalos.NET.Testing` and exercises the Anthropic adapter,
sync and streaming, and the Ollama generator.

**Breaking changes in 0.20.0:**

- **`Thalos.NET.Anthropic` no longer depends on the Anthropic SDK.** The SDK no longer flows to a host transitively; a
  host that uses SDK types itself must reference the `Anthropic` package directly. `RawRepresentation` on a response,
  its content and a streaming update now holds `ZeroAlloc.Rest.Anthropic` types instead of SDK types.
- **Cache markers set by the caller are ignored.** `WithCacheControl`, raw SDK content blocks, SDK `ToolUnion` tools
  and a `RawRepresentationFactory` returning `MessageCreateParams` no longer reach the wire. The prompt-cache
  translator places every breakpoint from `PromptCacheHints`, up to four, so the limit can no longer be exceeded by a
  caller.

**Added in 0.20.0:**

- **`Thalos.NET.Ollama`:** `OllamaEmbeddingGenerator`, an `IEmbeddingGenerator<string, Embedding<float>>` over
  `POST /api/embed` that reports the prompt token count as usage, and `services.UseOllamaEmbeddings(endpoint, model)`,
  which registers it as the singleton generator and returns the same instance. See the README,
  [Security](../README.md#security-ai-sentinel-needs-an-embedding-generator-for-full-coverage).

**Changed in 0.20.0:**

- **The Anthropic defaults are the SDK's:** a request attempt may take ten minutes, and a 429, 529 or other 5xx
  response is retried twice, honouring `retry-after`. `Thalos:Anthropic:Timeout` and `Thalos:Anthropic:MaxRetries`
  still override them. A negative `MaxRetries`, or a `Timeout` that is zero or negative but not infinite, now fails
  options validation at startup.
- **A stream that ends without `message_stop` throws** an `AnthropicApiException` of type `stream_incomplete`, instead
  of ending as a short answer that looks complete.
- **Tool schemas are reshaped as SDK 13.3.0 did,** so the tools a model sees are unchanged.
- **A system message in the middle of a conversation** is lifted into the top-level system prompt, in order, ahead of
  `ChatOptions.Instructions`; the Messages API has no system role inside `messages`.
- **A tool result is sent as text.** A result with several MCP text items sends them joined by newlines, in one
  `tool_result` block.

## Local development against a consumer (Daedalus)

`scripts/pack-local.ps1` packs `0.3.0-local.<timestamp>` (the `VersionPrefix` in `Directory.Build.props`) into `C:\Projects\Prive\.nuget-local`
(no GitVersion involved) — the consumer pins that exact version until the release is on nuget.org.

## Renovate

`renovate.json` ignores `dotnet-sdk` on purpose: `global.json` pins the lowest 10.0.x feature band with
`rollForward: latestFeature` so both dev machines and CI resolve; a bumped pin above locally installed
SDKs breaks local builds. Everything else is bumped by PRs that must pass the same gates.

## Schema migrations and rolling deploys

`Thalos.NET.Workflow.Orm` ships SQL migrations (`WorkflowOrmMigrations.Postgres`). By default
`WorkflowOrmOptions.EnsureSchemaOnStartup` applies them from the host at startup, which means the first
instance to start applies anything pending while the other instances are still running the code they were
deployed with.

That is safe for a purely additive migration. It is **not** safe for a migration older code cannot write
against, and there is one of those:

### 1004 — `process_definition.content_hash`

Adds `content_hash` as `NOT NULL` with no default, to make a stored process version immutable: re-syncing the
same `(process, version)` with different content is refused instead of rewriting a definition a live run is
pinned to.

**Apply this migration and deploy the matching code as one step. Do not apply it ahead of the rollout.**

PostgreSQL validates `NOT NULL` against the proposed tuple *before* conflict resolution, so an instance running
pre-1004 code — whose `INSERT` never mentions the column — fails with `23502` on **every**
`UpsertAndActivateAsync`, the `ON CONFLICT` path included. In a rolling deploy where one instance migrates
first, every instance not yet replaced loses process syncing for as long as it is still running. Nothing
already-running breaks — runs, resumes and dispatch are unaffected, since they only read — but no process
definition can be synced or activated from an old instance.

For a deployment that rolls instances one at a time across this migration, turn `EnsureSchemaOnStartup` off and
apply the schema change as an explicit step, rather than letting whichever instance wins the startup race
decide when the rest start failing.

### Behaviour change that ships with it

Editing a process file **without bumping its `version`** is now a sync *error* rather than a silent overwrite.
Authors who relied on re-syncing a version in place must bump the version instead. The error names the process
and version and says so.

### 1011 — `create_workflow_run_listing_index` (0.16.0)

Adds `ix_workflow_run_created_at_id` on `workflow_run (created_at DESC, id DESC)`, which `IWorkflowRunQuery` reads
newest first with keyset paging. It is additive and safe in a rolling deploy: older code ignores an index. It is not
built `CONCURRENTLY`, so it briefly blocks writes to `workflow_run` while it builds.

### Outbox migration 2 — `add_outbox_lease`

ZeroAlloc.Outbox 3.0 adds migration 2 to `OutboxOrmMigrations.Postgres`: nullable `LockedBy` and `LockedUntil`
columns on `outboxmessages`, which the 3.0 outbox claims and marks through. `EnsureSchemaOnStartup` applies it with
the rest. It is additive, and 2.x consumers ignore the columns, so it is safe to apply ahead of the rollout.

Apply it **before** any 3.0 outbox consumer starts, because a 3.0 claim fails against a table without the columns.
Then replace every consumer promptly: a 2.x consumer ignores leases, so while 2.x and 3.0 consumers run side by
side they can dispatch the same message twice.

Versions 1 (`create_outbox_messages`) and 2 (`add_outbox_lease`) belong to the outbox. Every migration source run
against the database shares one history table, `__zaorm_migrations`, keyed on version alone, which is why
`WorkflowOrmMigrations` starts at 1001. A host's own migration at version 1 or 2, already applied or not, now stops startup: since
ZeroAlloc.ORM 2.0, `MigrationRunner` throws `ZeroAllocOrmMigrationConflictException` for a version already applied
under a different name, where 1.x silently skipped the colliding migration and never ran its SQL. Move such a
migration out of the outbox's range before upgrading.

## Process-file keys that are parsed but not yet honoured

`ProcessNode` carries three properties the loader populates from the YAML and nothing in the engine reads:
`models:`, `lenses:` and `quorum:`. No validator rule inspects them and `WorkflowNodeDispatcher` never looks at
them. A node written as `models: [sonnet, opus]` therefore executes **once**, against whatever single model the
resolved agent is configured with, and the fan-out is silently ignored — no warning at load time, nothing in the
event log. They are reserved for a later phase; treat any process file using them as declaring intent, not
behaviour.

## Process files that used to sync and now report an error

`ProcessValidator` gained three rules this phase, and `ProcessDefinitionSync` validates before it activates. A
process file that previously synced cleanly can now be rejected — with the version already active left running
untouched, as for any other validation failure. The three shapes:

- **A terminal node that also declares `next`, `branch` or `outcomes`.** `Advance` resolves the outgoing edge
  before it ever reaches the terminal status, so the run took that edge and the node's terminal status was
  unreachable.
- **A declared outcome that is blank or repeated.** These become the closed `enum` of the outcome tool the node's
  agent is offered, and `OutcomeTool.Validate` refuses both — so the node failed on every dispatch.
- **An `onExceeded` redirect from which the capped node is reachable again.** The cap fired, redirected, and was
  routed straight back into the node it had just capped, forever.

Every file this newly rejects was already broken at run time — each shape either never terminated or failed on
every dispatch of the node — so nothing that used to work stops working. What changes is *when* you find out:
at sync, naming the node, instead of after a run had spent the agent turns to reach it. A deployer rolling out
this version should expect `SyncAsync` to start reporting errors for such files, and should read the error rather
than assume the sync itself regressed.
