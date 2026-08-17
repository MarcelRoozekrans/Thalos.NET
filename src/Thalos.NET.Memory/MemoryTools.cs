using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Memory;

/// <summary>
/// The <c>memory</c> tool source's methods. Owner and agent always come from the ambient <see cref="TurnScope"/> — never
/// from parameters — and the tools never write under the host's shared owner. Results are short strings for the model;
/// errors are reported as text, never thrown.
/// </summary>
[ThalosToolType]
public sealed class MemoryTools(IMemoryService memory, IOptions<MemoryOptions> options)
{
    private const string NoCaller = "Memory tools are only available to an authenticated caller inside an agent turn.";
    private const int ListPageSize = 20;
    private const int PreviewLength = 200;

    [ThalosTool("remember")]
    [Description("Store a durable memory about the user or the work (a fact, preference, decision, learning or note) so it can be recalled in later conversations. One idea per memory.")]
    public async Task<string> RememberAsync(
        [Description("The memory text (max 4000 characters).")] string text,
        [Description("fact | preference | decision | learning | note (default note).")] string? kind = null,
        [Description("Up to 10 short tags.")] string[]? tags = null,
        [Description("Importance 0..1 (default 0.5).")] double? importance = null,
        [Description("true (default) = visible to all of the owner's agents; false = only this agent.")] bool shared = true,
        CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return NoCaller;
        }

        if (!MemoryKind.TryParse(kind ?? MemoryKind.Note.Value, out var memoryKind))
        {
            return $"Could not remember: unknown kind '{kind}'. Use fact, preference, decision, learning or note.";
        }

        var result = await memory.RememberAsync(new RememberRequest
        {
            OwnerId = caller.OwnerId,
            AgentId = shared ? null : caller.AgentId,
            Text = text,
            Kind = memoryKind,
            Tags = tags ?? [],
            Importance = importance ?? 0.5,
            Source = "tool:memory__remember",
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return $"Could not remember: {result.Error.Message}";
        }

        var suffix = result.Value.IndexPending ? " Note: not yet searchable (memory index unavailable)." : "";
        return $"Remembered {result.Value.Id} ({result.Value.Kind.Value}).{suffix}";
    }

    [ThalosTool("recall")]
    [Description("Search long-term memory for information relevant to a query. Returns the best matches with their ids (use memory__forget with an id to archive one).")]
    public async Task<string> RecallAsync(
        [Description("What to look for.")] string query,
        [Description("Max results, 1..20 (default 5).")] int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return NoCaller;
        }

        var o = options.Value;
        var recall = new RecallOptions { TopK = Math.Clamp(topK ?? o.Recall.TopK, 1, 20), MinScore = o.Recall.MinScore, MaxChars = o.Recall.MaxChars };
        var result = await memory.RecallAsync(query, new MemoryScope(caller.OwnerId, caller.AgentId, o.SharedOwnerId), recall, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return $"Could not recall: {result.Error.Message}";
        }

        if (result.Value.Count == 0)
        {
            return "No relevant memories.";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < result.Value.Count; i++)
        {
            var m = result.Value[i];
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. [{m.Record.Kind.Value} · {m.Score:0.00} · {m.Record.Id}] {m.Record.Text}").Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    // Task 17 replaces these stubs with the real forget + list.
    [ThalosTool("forget")]
    [Description("Archive one of the caller's own memories by id.")]
    public static string Forget([Description("The memory id.")] string id) => NoCaller;

    [ThalosTool("list")]
    [Description("List the caller's memories.")]
    public static string List([Description("fact | preference | decision | learning | note; omit for all.")] string? kind = null, [Description("1-based page (default 1).")] int? page = null) => NoCaller;

    /// <summary>The turn's owner and agent, or null when there is no turn or the caller is anonymous.</summary>
    internal static (string OwnerId, AgentId? AgentId)? Caller()
    {
        var scope = TurnScope.Current;
        if (scope is null || string.IsNullOrWhiteSpace(scope.Caller.Id) || string.Equals(scope.Caller.Id, AnonymousSecurityContext.AnonymousId, StringComparison.Ordinal))
        {
            return null;
        }

        return (scope.Caller.Id, scope.AgentId == default ? null : scope.AgentId);
    }
}
