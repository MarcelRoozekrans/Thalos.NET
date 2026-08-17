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

    [ThalosTool("forget")]
    [Description("Archive one of the caller's own memories by id (ids come from memory__recall or memory__list). Archived memories are no longer recalled.")]
    public async Task<string> ForgetAsync([Description("The memory id.")] string id, CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return NoCaller;
        }

        if (!MemoryId.TryParse(id, null, out var memoryId))
        {
            return "Invalid memory id.";
        }

        // no shared owner in the scope: the tool archives the caller's own memories only, never the host's project memories
        var result = await memory.ForgetAsync(memoryId, new MemoryScope(caller.OwnerId, caller.AgentId, null), hard: false, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? $"Archived memory {memoryId}." : $"Could not forget: {result.Error.Message}";
    }

    [ThalosTool("list")]
    [Description("List the caller's memories (own and shared project memories), newest first, 20 per page, optionally filtered by kind.")]
    public async Task<string> ListAsync(
        [Description("fact | preference | decision | learning | note; omit for all.")] string? kind = null,
        [Description("1-based page (default 1).")] int? page = null,
        CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return NoCaller;
        }

        IReadOnlyList<MemoryKind>? kinds = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!MemoryKind.TryParse(kind, out var parsed))
            {
                return $"Could not list: unknown kind '{kind}'.";
            }

            kinds = [parsed];
        }

        var o = options.Value;
        IReadOnlyList<string> owners = o.SharedOwnerId is { } shared && !string.Equals(shared, caller.OwnerId, StringComparison.Ordinal) ? [caller.OwnerId, shared] : [caller.OwnerId];
        var result = await memory.ListAsync(new MemoryQuery { OwnerIds = owners, Kinds = kinds, Page = Math.Max(1, page ?? 1), PageSize = ListPageSize }, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return $"Could not list: {result.Error.Message}";
        }

        var p = result.Value;
        var pages = Math.Max(1, (p.TotalCount + p.PageSize - 1) / p.PageSize);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{p.TotalCount} memories (page {p.Page}/{pages}):");
        foreach (var r in p.Items)
        {
            var text = r.Text.Length <= PreviewLength ? r.Text : string.Concat(r.Text.AsSpan(0, PreviewLength), "…");
            sb.Append('\n').Append(CultureInfo.InvariantCulture, $"- [{r.Kind.Value} · {r.Id}] {text}");
        }

        return sb.ToString();
    }

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
