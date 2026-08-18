using System.Collections.Concurrent;
using System.Text;
using Thalos.Agents;
using ZeroAlloc.Inject;

namespace Thalos.Skills;

/// <summary>
/// The rendered <c>&lt;skills&gt;</c> block, cached per glob set. <see cref="SkillSyncService"/> calls <see cref="Set"/> once
/// per sync; every turn is then a dictionary lookup rather than a query. <see cref="Set"/> may run while a turn is rendering:
/// the snapshot and its cache are swapped as one pair, so a render that loses the race returns the snapshot it captured and
/// cannot leave that block behind for later turns. Rendering is deterministic: entries are sorted by name
/// and the block is capped by the configured budget with an explicit "… and N more" line — truncation is never silent.
/// </summary>
/// <remarks>Not sealed: a host may override <see cref="Render"/>; the default implementation never throws.</remarks>
[Singleton(As = typeof(SkillCatalogue))] // registered by UseSkills through the generated AddThalosSkillsServices()
public class SkillCatalogue
{
    private volatile State _state = new(new Snapshot([], 0), NewCache());

    /// <summary>Replaces the catalogue's contents (active skills only, any order) and the per-glob-set cache with it.</summary>
    public void Set(IReadOnlyList<SkillDocument> skills, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var sorted = skills.OrderBy(s => s.Name).ToList();

        // A new dictionary, not Clear(): the snapshot and its cache are swapped as one immutable pair. Clearing a
        // shared dictionary leaves a Render that already captured the previous snapshot free to insert its now-stale
        // block into the cache this call just emptied, where nothing would ever evict it again — every later turn
        // would read the old catalogue for the rest of the process. A render that loses the race can now only write
        // into the dictionary that belongs to the snapshot it rendered, which nobody reads any more.
        _state = new State(new Snapshot(sorted, maxChars), NewCache());
    }

    /// <summary>The block for an agent whose <see cref="AgentDefinition.Skills"/> are <paramref name="globs"/>, or null when nothing matches.</summary>
    public virtual string? Render(IReadOnlyList<string> globs)
    {
        ArgumentNullException.ThrowIfNull(globs);
        var state = _state;
        if (state.Snapshot.Skills.Count == 0 || globs.Count == 0)
        {
            return null;
        }

        return state.Rendered.GetOrAdd(CacheKey(globs), static (_, s) => RenderCore(Matching(s.Snapshot.Skills, s.Globs), s.Snapshot.MaxChars), (state.Snapshot, Globs: globs));
    }

    private static ConcurrentDictionary<string, string?> NewCache() => new(StringComparer.Ordinal);

    /// <summary>The active skills matching <paramref name="globs"/>, sorted by name.</summary>
    internal static List<SkillDocument> Matching(IReadOnlyList<SkillDocument> skills, IReadOnlyList<string> globs)
    {
        var matching = new List<SkillDocument>(skills.Count);
        for (var i = 0; i < skills.Count; i++)
        {
            if (IsAllowed(globs, skills[i].Name.Value))
            {
                matching.Add(skills[i]);
            }
        }

        return matching;
    }

    /// <summary>Whether <paramref name="name"/> matches any of <paramref name="globs"/> (the same matcher tool globs use).</summary>
    internal static bool IsAllowed(IReadOnlyList<string> globs, string name)
    {
        // plain loop, not LINQ Any(): ZA0601 rejects the per-iteration closure in this path
        for (var i = 0; i < globs.Count; i++)
        {
            if (Thalos.Tools.Glob.IsMatch(globs[i], name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders <paramref name="matching"/> within <paramref name="maxChars"/> (0 or less = no budget). Entries are whole or
    /// absent; whatever is left out is counted on an explicit trailing line. The budget is exceeded only when the tags plus
    /// that one line already exceed it, in which case no entry is listed at all — the count is still right.
    /// </summary>
    internal static string? RenderCore(List<SkillDocument> matching, int maxChars)
    {
        if (matching.Count == 0)
        {
            return null;
        }

        var used = SkillBlock.CatalogueOpen.Length + 1 + SkillBlock.CatalogueClose.Length;
        var taken = new List<string>(matching.Count);
        for (var i = 0; i < matching.Count; i++)
        {
            var line = "- " + matching[i].Name.Value + ": " + SkillBlock.SanitizeLine(matching[i].Description) + "\n";
            var remainingAfter = matching.Count - (i + 1);
            var overflow = remainingAfter > 0 ? SkillBlock.Overflow(remainingAfter).Length + 1 : 0;
            if (maxChars > 0 && used + line.Length + overflow > maxChars)
            {
                break;
            }

            used += line.Length;
            taken.Add(line);
        }

        var sb = new StringBuilder(used);
        sb.Append(SkillBlock.CatalogueOpen).Append('\n');
        for (var i = 0; i < taken.Count; i++)
        {
            sb.Append(taken[i]);
        }

        if (taken.Count < matching.Count)
        {
            sb.Append(SkillBlock.Overflow(matching.Count - taken.Count)).Append('\n');
        }

        return sb.Append(SkillBlock.CatalogueClose).ToString();
    }

    // Length-prefixed, not merely separated. AgentDefinition.Skills is an unvalidated
    // IReadOnlyList<string>, so a glob may contain any character including the separator itself:
    // ["release", "dotnet-*"] and the single glob "release\u001Fdotnet-*" would otherwise share one
    // cache entry, and with it one agent's catalogue. Prefixing the count and each length makes the
    // encoding injective whatever the globs contain.
    private static string CacheKey(IReadOnlyList<string> globs)
    {
        var sb = new StringBuilder().Append(globs.Count);
        for (var i = 0; i < globs.Count; i++)
        {
            sb.Append('\u001F').Append(globs[i].Length).Append('\u001F').Append(globs[i]);
        }

        return sb.ToString();
    }

    private sealed record Snapshot(IReadOnlyList<SkillDocument> Skills, int MaxChars);

    /// <summary>A snapshot and the cache of blocks rendered from it — swapped together so the two can never disagree.</summary>
    private sealed record State(Snapshot Snapshot, ConcurrentDictionary<string, string?> Rendered);
}
