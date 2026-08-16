# Thalos.Sample.Console

A minimal REPL: one `Architect` agent backed by Anthropic Claude, scanned by AI.Sentinel, with the
[roslyn-codelens](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp) MCP server exposed as `roslyn__*` tools.
Mutating tools (`roslyn__apply_*`, `roslyn__rename_*`) are gated behind a `developer` policy.

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

`/quit` or `/exit` (or Ctrl+Z / Ctrl+D) ends the session.

## What to look at

- `Program.cs` — the whole wiring is one `AddThalos(...)` call: `UseAnthropic(configuration)`, `UseAISentinel(...)`,
  `UseInMemorySessionStore()`, `AddMcpServersFromFile(...)`, `RequireToolPolicy(...)`, `AddPolicy<DeveloperPolicy>()`,
  `AddAgent(...)`.
- `DeveloperPolicy` — a plain ZeroAlloc.Authorization `[Policy("developer")]`; Thalos looks it up by name and enforces it
  at the tool boundary, before the tool runs.
- `ConsoleCaller` — the `ISecurityContext` the channel supplies with every turn. Thalos never infers the caller.
- The event switch — `TextDeltaEvent`, `ToolCallStartedEvent`, `ToolCallFinishedEvent`, `UsageEvent`, `TurnFailedEvent` are
  the same events a web channel would forward as SSE.

## Security note: AI.Sentinel needs an embedding generator

AI.Sentinel 2.0.1's *security* detectors (prompt injection, jailbreak, data exfiltration, …) are semantic: they need
`SentinelOptions.EmbeddingGenerator`. This sample does **not** wire one, so only the lexical/operational detectors run
(Sentinel logs a warning when the pipeline is first built). A real host should supply an `IEmbeddingGenerator` (Ollama,
OpenAI, …) in the `UseAISentinel(o => o.EmbeddingGenerator = …)` callback.

## Manual smoke test

This sample is not run in CI (it needs a real key and a local solution). After changing the runtime, run it once with a
real key and save the transcript under `docs/samples/console-smoke-YYYY-MM-DD.md`.
