# Thalos.NET

> Named after Talos, the bronze guardian of Crete. Spelled *Thalos* because `Talos.*` is taken on nuget.org.

A Hermes-style, ZeroAlloc-native agent framework for .NET, built on
[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) with first-class
[AI.Sentinel](https://github.com/MarcelRoozekrans/AI.Sentinel) security and
[Model Context Protocol](https://modelcontextprotocol.io) tools.

| Package | Purpose |
|---|---|
| `Thalos.NET.Abstractions` | Ports and models — no framework dependencies |
| `Thalos.NET` | Runtime: agent factory, tool catalog, session state machine, in-memory store |
| `Thalos.NET.Testing` | `ScriptedChatClient`, session-store contract tests |
| `Thalos.NET.Mcp` | MCP servers (stdio / http) as tool sources |
| `Thalos.NET.Anthropic` | Anthropic Claude provider |
| `Thalos.NET.Sentinel` | AI.Sentinel at the model boundary |

## Quick start

```csharp
services.AddThalos(thalos => thalos
    .UseAnthropic(apiKey: Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!, defaultModel: "claude-sonnet-4-5")
    .UseAISentinel()
    .UseInMemorySessionStore()
    .AddMcpServersFromFile(".mcp.json")
    .AddAgent(new AgentDefinition
    {
        Id = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV"),
        Name = "Architect",
        Instructions = "You are a senior .NET architect. Use the roslyn tools to answer precisely.",
        Tools = ["roslyn__*"],
    }));

var runtime = provider.GetRequiredService<IAgentRuntime>();
var session = await runtime.CreateSessionAsync(agentId, caller, ct);
var turn = await runtime.RunTurnAsync(new AgentTurnRequest(session.Value, "Who calls TaskRepository.UpdateAsync?", caller), ct);
```

Status: **0.1.0 — API is unstable until 1.0.**
