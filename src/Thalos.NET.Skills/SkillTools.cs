using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Options;
using Thalos.Runtime;

namespace Thalos.Skills;

/// <summary>
/// The <c>skills</c> tool source's methods. Which skills exist for the caller is decided entirely by the turn's agent
/// (<see cref="TurnScope.AgentId"/> → <see cref="IAgentCatalog"/> → <see cref="AgentDefinition.Skills"/>); a name outside those
/// globs answers exactly like a name that does not exist, so an agent cannot probe what other agents can do. Results are short
/// strings for the model and errors are reported as text, never thrown.
/// </summary>
[ThalosToolType]
public sealed class SkillTools(ISkillStore store, ISkillIndex index, IAgentCatalog agents, IOptions<SkillOptions> options)
{
    // The catalogue is authoritative whatever search says, so every "no result" answer points back at it.
    private const string CatalogueTail = "the <skills> block in your instructions lists every skill you can load.";

    private const string UnavailableText = "Skill search is unavailable; " + CatalogueTail;

    private const string NoMatchText = "No matching skills. The <skills> block in your instructions lists every skill you can load.";

    // Concatenated rather than string.Format: a cached CompositeFormat (CA1863) buys nothing on a two-piece message.
    private const string UnknownTail = "'. The <skills> block in your instructions lists the ones you can load; skills__search finds them by what they do.";

    /// <summary><c>skills__load</c>: the full text of one skill the turn's agent is allowed to load.</summary>
    [ThalosTool("load")]
    [Description("Load the full text of a skill (a procedure document) by name. Names come from the <skills> block in your instructions or from skills__search.")]
    public async Task<string> LoadAsync(
        [Description("The skill name, e.g. dotnet-migrations.")] string name,
        CancellationToken cancellationToken = default)
    {
        var globs = Globs();
        if (!SkillName.TryParse(name, out var skill) || !SkillCatalogue.IsAllowed(globs, skill.Value))
        {
            return UnknownText(name);
        }

        // SkillQuery.IncludeInactive stays false. ISkillStore.GetAsync deliberately returns a skill whose file has been
        // deleted and leaves the decision to the caller; this is that caller, and a retired procedure must never resurface.
        var found = await store.ListAsync(new SkillQuery { Names = [skill] }, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return $"Could not load skill '{skill.Value}': {found.Error.Message}";
        }

        if (found.Value.Count == 0)
        {
            return UnknownText(name);
        }

        var body = SkillBlock.SanitizeBody(found.Value[0].Body).TrimEnd();
        return string.Concat(SkillBlock.SkillOpen(skill), "\n", body, "\n", SkillBlock.SkillClose);
    }

    /// <summary><c>skills__search</c>: ranked <c>name: description</c> lines for the skills this agent may load. Never returns bodies.</summary>
    /// <remarks>
    /// Hits are filtered by the agent's globs <em>after</em> ranking (the index has no notion of an agent), so a search whose
    /// best hits all belong to other agents can return fewer than <paramref name="topK"/> rows. The catalogue stays
    /// authoritative. <paramref name="topK"/> comes from the model, so it is clamped to 1..20 into a local copy —
    /// <see cref="SkillOptions.Search"/> is a bound singleton and is never written to.
    /// </remarks>
    /// <param name="query">Free text describing what the caller needs to do.</param>
    /// <param name="topK">The model's requested result count, or null for the configured default.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Text for the model: ranked rows, or a plain sentence saying why there are none.</returns>
    [ThalosTool("search")]
    [Description("Search the skills available to this agent by what they do. Returns matching names with their descriptions; use skills__load to read one.")]
    public async Task<string> SearchAsync(
        [Description("What you need to do, in your own words.")] string query,
        [Description("Max results, 1..20 (default 5).")] int? topK = null,
        CancellationToken cancellationToken = default)
    {
        var globs = Globs();
        if (globs.Count == 0)
        {
            return "No skills are available to this agent.";
        }

        // A copy: options.Value.Search is bound configuration shared by every turn, so the clamp lands on a local.
        var configured = options.Value.Search;
        var search = new SkillSearchOptions { TopK = Math.Clamp(topK ?? configured.TopK, 1, 20), MinScore = configured.MinScore };
        var hits = await index.SearchAsync(query, search, cancellationToken).ConfigureAwait(false);
        if (hits.IsFailure)
        {
            return hits.Error.Code == AgentErrorCode.SkillSearchUnavailable
                ? UnavailableText
                : "Could not search skills: " + hits.Error.Message;
        }

        var names = new List<SkillName>(hits.Value.Count);
        for (var i = 0; i < hits.Value.Count; i++)
        {
            if (SkillCatalogue.IsAllowed(globs, hits.Value[i].Name.Value))
            {
                names.Add(hits.Value[i].Name);
            }
        }

        if (names.Count == 0)
        {
            return NoMatchText;
        }

        var found = await store.ListAsync(new SkillQuery { Names = names }, cancellationToken).ConfigureAwait(false);
        return found.IsFailure ? "Could not search skills: " + found.Error.Message : Render(names, found.Value);
    }

    /// <summary>Ranked lines in hit order (the store returns them sorted by name), descriptions only — never a body.</summary>
    /// <param name="ranked">The allowed hits, best first.</param>
    /// <param name="found">The documents the store returned for <paramref name="ranked"/>, in its own order.</param>
    /// <returns>A header line followed by one <c>- name: description</c> row per skill that still exists and is active.</returns>
    private static string Render(List<SkillName> ranked, IReadOnlyList<SkillDocument> found)
    {
        var byName = new Dictionary<SkillName, SkillDocument>(found.Count);
        for (var i = 0; i < found.Count; i++)
        {
            byName[found[i].Name] = found[i];
        }

        var sb = new StringBuilder("Skills matching your query, best first — load one with skills__load:");
        for (var i = 0; i < ranked.Count; i++)
        {
            if (byName.TryGetValue(ranked[i], out var skill))
            {
                sb.Append("\n- ").Append(skill.Name.Value).Append(": ").Append(SkillBlock.SanitizeLine(skill.Description));
            }
        }

        return sb.ToString();
    }

    /// <summary>The skill globs of the agent running this turn; empty outside a turn or for an unregistered agent.</summary>
    internal IReadOnlyList<string> Globs()
    {
        var scope = TurnScope.Current;
        return scope is not null && scope.AgentId != default && agents.TryGet(scope.AgentId, out var agent) ? agent.Skills : [];
    }

    private static string UnknownText(string name) => string.Concat("Unknown skill '", name, UnknownTail);
}
