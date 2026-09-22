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
> release-please pushes the tag using the default `GITHUB_TOKEN`, and GitHub will not start a
> workflow from an event that token created — its guard against workflows triggering themselves
> forever. A tag trigger on `ci.yml` would never fire, and it would fail *silently*: no run, no
> error, a release that quietly never reaches nuget.org. A PAT would lift the restriction and was
> rejected, because it means a long-lived stored credential with write access in a repository whose
> release design is deliberately Trusted Publishing with no stored key. Do not "simplify" the call
> into a tag trigger.

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
