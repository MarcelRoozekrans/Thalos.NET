# Thalos.NET

[![CI](https://github.com/MarcelRoozekrans/Thalos.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/MarcelRoozekrans/Thalos.NET/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

> Named after Talos, the bronze guardian of Crete. Spelled *Thalos* because `Talos.*` is taken on nuget.org.

A Hermes-style, ZeroAlloc-native agent framework for .NET, built on
[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) with first-class
[AI.Sentinel](https://github.com/MarcelRoozekrans/AI.Sentinel) security and
[Model Context Protocol](https://modelcontextprotocol.io) tools.

| Package | Purpose | Depends on |
|---|---|---|
| `Thalos.NET.Abstractions` | Ports (`IAgentRuntime`, `IAgentSessionStore`, `IToolSource`, `IChatClientProvider`, …), models, typed ids, `AgentError` | ZeroAlloc.*, `Microsoft.Extensions.AI.Abstractions` |
| `Thalos.NET` | Runtime: agent factory, tool catalog + authorization, session state machine, in-memory store, `AddThalos(...)` | Abstractions, `Microsoft.Agents.AI` |
| `Thalos.NET.Testing` | `ScriptedChatClient`, `RecordingNotificationPublisher`, reusable `IAgentSessionStore` contract tests (ships xunit + AwesomeAssertions references by design) | Thalos.NET |
| `Thalos.NET.Mcp` | MCP servers (stdio / http / sse, Claude Code-style `.mcp.json`) as tool sources | Thalos.NET, `ModelContextProtocol` |
| `Thalos.NET.Anthropic` | Anthropic Claude chat-client provider | Thalos.NET, `Anthropic` |
| `Thalos.NET.Sentinel` | AI.Sentinel scanning at the model boundary, quarantine → `AgentError` | Thalos.NET, `AI.Sentinel` |

Targets `net8.0` and `net10.0`.

## Quick start

```csharp
services.AddThalos(thalos => thalos
    .UseAnthropic(configuration)                       // Thalos:Anthropic section; ApiKey falls back to ANTHROPIC_API_KEY
    .UseAISentinel(o => o.EmbeddingGenerator = myEmbeddings)   // see the security note below
    .UseInMemorySessionStore()
    .AddMcpServersFromFile(Path.Combine(AppContext.BaseDirectory, ".mcp.json"))
    .RequireToolPolicy("roslyn__apply_*", "developer")
    .AddPolicy<DeveloperPolicy>()                      // any ZeroAlloc.Authorization [Policy("developer")]
    .AddAgent(new AgentDefinition
    {
        Id = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", null),
        Name = "Architect",
        Instructions = "You are a senior .NET architect. Use the roslyn tools to answer precisely.",
        Tools = ["roslyn__*"],
    }));

var runtime = provider.GetRequiredService<IAgentRuntime>();
var session = await runtime.CreateSessionAsync(agentId, caller, ct);           // caller: ISecurityContext supplied by the channel
var turn = await runtime.RunTurnAsync(new AgentTurnRequest(session.Value, "Who calls TaskRepository.UpdateAsync?", caller), ct);

await foreach (var evt in runtime.RunTurnStreamingAsync(new AgentTurnRequest(session.Value, "…", caller), ct))
{
    // TextDeltaEvent, ToolCallStartedEvent, ToolCallFinishedEvent, UsageEvent, TurnCompletedEvent | TurnFailedEvent
}
```

Tool names exposed to the model are `{source}__{tool}` (e.g. `roslyn__find_callers`); `AgentDefinition.Tools` and
`RequireToolPolicy` take globs over that qualified name. Authorization is enforced by Thalos at the function boundary —
before the tool runs — not by inspecting the chat stream afterwards.

A runnable REPL lives in [`samples/Thalos.Sample.Console`](samples/Thalos.Sample.Console/README.md).

## Security: AI.Sentinel needs an embedding generator

AI.Sentinel 2.0.1's *security* detectors (prompt injection, jailbreak, exfiltration, …) are semantic. Without
`SentinelOptions.EmbeddingGenerator` they return *Clean* and only the lexical/operational detectors run — `UseAISentinel()`
with no embedding generator is **not** prompt-injection protection. Wire a real `IEmbeddingGenerator` (Ollama, OpenAI, …):

```csharp
.UseAISentinel(o =>
{
    o.EmbeddingGenerator = embeddingGenerator;   // IEmbeddingGenerator<string, Embedding<float>>
    o.OnCritical = SentinelAction.Quarantine;    // surfaces as AgentError.Quarantined ("<Severity>: <DetectorId>")
    o.OnHigh = SentinelAction.Alert;
})
```

## Local development against Daedalus

Until the packages are on nuget.org, consumers (Daedalus, phase 1.1) build from a local folder feed:

```powershell
pwsh scripts/pack-local.ps1          # → C:\Projects\Prive\.nuget-local\Thalos.NET*.0.1.0-local.<timestamp>.nupkg
```

The script prints the exact version. In the consuming repo:

`nuget.config` (next to the solution):
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="thalos-local" value="C:\Projects\Prive\.nuget-local" />
  </packageSources>
</configuration>
```

`Directory.Packages.props` (central package management):
```xml
<ItemGroup>
  <PackageVersion Include="Thalos.NET.Abstractions" Version="0.1.0-local.20260816120000" />
  <PackageVersion Include="Thalos.NET"              Version="0.1.0-local.20260816120000" />
  <PackageVersion Include="Thalos.NET.Testing"      Version="0.1.0-local.20260816120000" />
  <PackageVersion Include="Thalos.NET.Mcp"          Version="0.1.0-local.20260816120000" />
  <PackageVersion Include="Thalos.NET.Anthropic"    Version="0.1.0-local.20260816120000" />
  <PackageVersion Include="Thalos.NET.Sentinel"     Version="0.1.0-local.20260816120000" />
</ItemGroup>
```

Re-run the script and bump the pin after every change to Thalos.NET (NuGet caches by exact version, so never re-pack the
same version — the timestamp suffix guarantees a fresh one). If a stale package is still picked up, clear
`%USERPROFILE%\.nuget\packages\thalos.net*`.

## Building

```powershell
dotnet build              # 0 warnings — TreatWarningsAsErrors with Meziantou, Roslynator and ZeroAlloc analyzers
dotnet test               # unit, MCP (launches tests/Thalos.NET.Tests.McpServer over stdio), Sentinel, architecture
```

## Versioning and releases

Same setup as [Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET); the runbook is [docs/release.md](docs/release.md).

- Versions come from git history via [GitVersion](GitVersion.yml) (`dotnet tool restore && dotnet dotnet-gitversion`);
  nothing is hand-edited. Stable versions only — no prereleases are published.
- Releases are cut by [release-please](.github/workflows/release-please.yml) from conventional commits (enforced on
  PRs by commitlint): dispatch → review/merge the release PR → dispatch → `vX.Y.Z` tag + GitHub release.
- CI (`.github/workflows/ci.yml`) builds and tests on Ubuntu and Windows on every push/PR, packs and validates the six
  packages, and rehearses the nuget.org push against a local feed. Publishing to nuget.org is a manual dispatch with
  `publish_to_nuget=true` on the tagged release commit, using nuget.org Trusted Publishing (no stored API key).

Status: **0.1.0 — API is unstable until 1.0.**
