# Thalos.Sample.Console

A minimal REPL: one `Architect` agent backed by Anthropic Claude, scanned by AI.Sentinel, with the
[roslyn-codelens](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp) MCP server exposed as `roslyn__*` tools.
Mutating tools (`roslyn__apply_*`, `roslyn__rename_*`) are gated behind a `developer` policy. Long-term memory
(`Thalos.NET.Memory`) is enabled with the in-memory store and no embedding generator, so the `memory__*` tools work but
recall finds nothing (see the note below). Skills (`Thalos.NET.Skills`) are enabled too, with one procedure document in
`skills/`.

## Skills

`skills/console-help/SKILL.md` is a real skill: `UseSkills` syncs it into the in-memory `ISkillStore` at start-up and
appends the `<skills>` catalogue (its name and description, one line) to the agent's instructions on every turn, and the
agent reads the body with `skills__load`. `Skills = ["*"]` on the agent definition is what opts it in — unlike `Tools`,
the default is *empty*. The sync is one-way and there is no file watcher, so **edit the file and restart** to pick up a
change. Because this sample registers no embedding generator the index is `UnavailableSkillIndex`: the catalogue is
unaffected, but `skills__search` answers that search is unavailable and points the model back at the catalogue — which is
what a host without embeddings genuinely looks like, so it is shown rather than hidden. Both are visible in the first two
log lines of a run:

```
info: Thalos.Skills.SkillThalosBuilderExtensions[564]
      No IEmbeddingGenerator<string, Embedding<float>> is registered; skills__search is unavailable and the <skills> catalogue is the only way in
info: Thalos.Skills.SkillSyncService[560]
      Skill sync: 1 scanned, 1 upserted, 0 unchanged, 0 skipped, 0 deactivated
```

The sync runs in `IHostedLifecycleService.StartingAsync`, so the sample calls `await host.StartAsync()` before the first
turn (and `StopAsync()` on the way out) — building the host is not enough to run hosted services.

## Prerequisites

1. **Anthropic API key** — either of:
   ```powershell
   dotnet user-secrets set "Thalos:Anthropic:ApiKey" "sk-ant-..." --project samples/Thalos.Sample.Console
   # or
   $env:ANTHROPIC_API_KEY = "sk-ant-..."
   ```
2. **roslyn-codelens MCP server** — `.mcp.json` launches it with `dnx RoslynCodeLens.Mcp --yes` (the .NET 10 SDK's
   `dnx` downloads and runs the tool on demand). Alternatively install it once and point `.mcp.json` at the tool:
   ```powershell
   dotnet tool install -g RoslynCodeLens.Mcp
   ```
   then use `"command": "roslyn-codelens-mcp"` with `"args": ["C:/Projects/Prive/daedalus/Daedalus.sln"]`.
3. **A solution to analyse** — `.mcp.json` points at `C:/Projects/Prive/daedalus/Daedalus.sln`; change the path to any solution.
   Opening a large solution can take a while (`ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS` is set to 600).

`.mcp.json` and `appsettings.json` are copied to the output directory and read from `AppContext.BaseDirectory`, so
`dotnet run` works from any working directory.

## Run

```powershell
dotnet run --project samples/Thalos.Sample.Console
```

Try:

- `Who calls TaskRepository.UpdateAsync?` — a read-only roslyn tool call; you will see `⚙ roslyn__find_callers …`
  followed by the streamed answer and a token-usage line.
- `Apply the first available code action in TaskRepository.cs` — the model calls `roslyn__apply_code_action`, which the
  `developer` policy denies (`✗ … developer role required (run with --developer)`).
- Re-run with the role: `dotnet run --project samples/Thalos.Sample.Console -- --developer` and ask again.
- `Remember that I prefer xUnit over NUnit.` — the model calls `memory__remember`; you will see `✎ stored …` (or
  `⧗ stored … but not indexed`, because this sample registers no embedding generator — recall stays empty until one is
  wired: `builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(…)` before `AddThalos`).
- `How do I answer questions about this sample?` — the `console-help` skill is already in the model's `<skills>`
  catalogue, so it calls `⚙ skills__load {"name":"console-help"}` and answers from the procedure. Ask it to *search* for
  a skill instead and it is told search is unavailable (no embedding generator), with the catalogue as the fallback.

`/quit` or `/exit` (or Ctrl+Z / Ctrl+D) ends the session.

## What to look at

- `Program.cs` — the whole wiring is one `AddThalos(...)` call: `UseAnthropic(configuration)`, `UseAISentinel(...)`,
  `UseInMemorySessionStore()`, `UseMemory()`, `UseSkills(...)`, `AddMcpServersFromFile(...)`, `RequireToolPolicy(...)`,
  `AddPolicy<DeveloperPolicy>()`, `AddAgent(...)`.
- `skills/console-help/SKILL.md` — the frontmatter grammar (`name`, `description`, optional `tags: [a, b]`, all at
  column 0 and single-line) followed by the markdown body the agent reads.
- `DeveloperPolicy` — a plain ZeroAlloc.Authorization `[Policy("developer")]`; Thalos looks it up by name and enforces it
  at the tool boundary, before the tool runs.
- `ConsoleCaller` — the `ISecurityContext` the channel supplies with every turn. Thalos never infers the caller.
- The event switch — `TextDeltaEvent`, `ToolCallStartedEvent`, `ToolCallFinishedEvent`, `UsageEvent`, `TurnFailedEvent`,
  the memory events (`MemoryRecalledEvent`, `MemoryStoredEvent`, `MemoryIndexPendingEvent`, `MemoryRecallFailedEvent`) and
  `SkillCatalogueFailedEvent` are the same events a web channel would forward as SSE.

## Security note: AI.Sentinel needs an embedding generator

AI.Sentinel 2.0.1's *security* detectors (prompt injection, jailbreak, data exfiltration, …) are semantic: they need
`SentinelOptions.EmbeddingGenerator`. This sample does **not** wire one, so only the lexical/operational detectors run
(Sentinel logs a warning when the pipeline is first built). A real host should supply an `IEmbeddingGenerator` (Ollama,
OpenAI, …) in the `UseAISentinel(o => o.EmbeddingGenerator = …)` callback.

## Manual smoke test

This sample is not run in CI (it needs a real key and a local solution). After changing the runtime, run it once with a
real key and save the transcript under `docs/samples/console-smoke-YYYY-MM-DD.md`.
