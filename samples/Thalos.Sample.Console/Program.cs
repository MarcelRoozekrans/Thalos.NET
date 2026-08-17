using AI.Sentinel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Anthropic;
using Thalos.Mcp;
using Thalos.Memory;
using Thalos.Sample;
using Thalos.Sentinel;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>();

var architect = new AgentDefinition
{
    Id = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", null),
    Name = "Architect",
    Description = "Answers questions about a .NET solution using Roslyn.",
    Instructions = """
        You are a senior .NET architect. Use the roslyn__* tools to answer precisely; cite symbols and files.
        Never guess: if a tool call fails, say so.
        Use memory__remember for durable facts and preferences the user states, and memory__recall before answering questions about earlier work.
        """,
    Tools = ["roslyn__*", "memory__*"],
};

builder.Services.AddThalos(thalos => thalos
    .UseAnthropic(builder.Configuration)
    // NOTE: AI.Sentinel 2.0.1's security detectors (prompt injection, jailbreak, exfiltration, …) are embedding-based.
    // Without SentinelOptions.EmbeddingGenerator only the lexical/operational detectors run — this sample deliberately
    // does not wire an embedding provider; a real host should (e.g. Ollama or OpenAI embeddings).
    .UseAISentinel(o =>
    {
        o.OnCritical = SentinelAction.Quarantine;
        o.OnHigh = SentinelAction.Alert;
    })
    .UseInMemorySessionStore()
    // Long-term memory: auto-recall block + memory__* tools. Without an IEmbeddingGenerator<string, Embedding<float>> in DI the
    // index is UnavailableMemoryIndex: memory__remember stores (IndexPending — see the ⧗ line), memory__recall finds nothing.
    // Register one (Ollama, OpenAI, …) for semantic recall; the in-memory store forgets everything when the process ends.
    .UseMemory()
    .AddMcpServersFromFile(Path.Combine(AppContext.BaseDirectory, ".mcp.json"))
    .RequireToolPolicy("roslyn__apply_*", "developer")
    .RequireToolPolicy("roslyn__rename_*", "developer")
    .AddPolicy<DeveloperPolicy>()
    .AddAgent(architect));

using var host = builder.Build();
var runtime = host.Services.GetRequiredService<IAgentRuntime>();
var caller = new ConsoleCaller(Environment.UserName, roles: args.Contains("--developer", StringComparer.Ordinal) ? ["developer"] : []);

var session = await runtime.CreateSessionAsync(architect.Id, caller, CancellationToken.None);
if (session.IsFailure)
{
    Console.Error.WriteLine(session.Error);
    return 1;
}

Console.WriteLine($"Session {session.Value}. Type a question, or /quit.");

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() is "/quit" or "/exit")
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    await foreach (var evt in runtime.RunTurnStreamingAsync(new AgentTurnRequest(session.Value, line, caller), CancellationToken.None))
    {
        switch (evt)
        {
            case TextDeltaEvent t:
                Console.Write(t.Text);
                break;
            case ToolCallStartedEvent c:
                Console.WriteLine($"\n  ⚙ {c.ToolName} {c.ArgumentsJson}");
                break;
            case ToolCallFinishedEvent f:
                Console.WriteLine($"  {(f.Succeeded ? "✓" : "✗")} {f.ToolName} ({f.Elapsed.TotalMilliseconds:F0} ms) {f.ResultPreview}");
                break;
            case UsageEvent u:
                Console.WriteLine($"\n  [in {u.Usage.InputTokens} / out {u.Usage.OutputTokens} tokens]");
                break;
            case TurnFailedEvent e:
                Console.WriteLine($"\n  ✗ {e.Error}");
                break;
            case MemoryRecalledEvent r:
                Console.WriteLine($"  ⟲ recalled {r.Count} memories ({r.Chars} chars)");
                break;
            case MemoryStoredEvent s:
                Console.WriteLine($"  ✎ stored {s.MemoryId} ({s.MemoryKind}{(s.Deduped ? ", deduped" : "")})");
                break;
            case MemoryIndexPendingEvent p:
                Console.WriteLine($"  ⧗ stored {p.MemoryId} but not indexed (no embedding generator — recall will not find it)");
                break;
            case MemoryRecallFailedEvent rf:
                Console.WriteLine($"  ⚠ memory recall failed: {rf.Code} (the turn continues without memories)");
                break;
            default:
                break;
        }
    }

    Console.WriteLine();
}

return 0;

namespace Thalos.Sample
{
    /// <summary>The console user; the <c>--developer</c> switch grants the "developer" role.</summary>
    internal sealed class ConsoleCaller(string id, string[] roles) : ISecurityContext
    {
        public string Id => id;
        public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Gates the mutating roslyn tools (<c>roslyn__apply_*</c>, <c>roslyn__rename_*</c>) behind the "developer" role.</summary>
    [Policy("developer")]
    internal sealed class DeveloperPolicy : IAuthorizationPolicy
    {
        public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default) =>
            new(ctx.Roles.Contains("developer")
                ? UnitResult<AuthorizationFailure>.Success()
                : UnitResult<AuthorizationFailure>.Failure(new AuthorizationFailure("role", "developer role required (run with --developer)")));
    }
}
