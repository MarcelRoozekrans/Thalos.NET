using System.ComponentModel;
using Thalos.Runtime;

namespace Thalos.Skills;

/// <summary>
/// The <c>skills</c> tool source's methods. Which skills exist for the caller is decided entirely by the turn's agent
/// (<see cref="TurnScope.AgentId"/> → <see cref="IAgentCatalog"/> → <see cref="AgentDefinition.Skills"/>); a name outside those
/// globs answers exactly like a name that does not exist, so an agent cannot probe what other agents can do. Results are short
/// strings for the model and errors are reported as text, never thrown.
/// </summary>
[ThalosToolType]
public sealed class SkillTools(ISkillStore store, IAgentCatalog agents)
{
    // Interpolated rather than string.Format: a cached CompositeFormat (CA1863) buys nothing on a two-piece message.
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
    /// <remarks>Task 19 gives this a body over <c>ISkillIndex</c>; until then every host answers as if it had no index.</remarks>
    [ThalosTool("search")]
    [Description("Search the skills available to this agent by what they do. Returns matching names with their descriptions; use skills__load to read one.")]
    public static Task<string> SearchAsync(
        [Description("What you need to do, in your own words.")] string query,
        [Description("Max results, 1..20 (default 5).")] int? topK = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(UnavailableSkillIndex.Reason);

    /// <summary>The skill globs of the agent running this turn; empty outside a turn or for an unregistered agent.</summary>
    internal IReadOnlyList<string> Globs()
    {
        var scope = TurnScope.Current;
        return scope is not null && scope.AgentId != default && agents.TryGet(scope.AgentId, out var agent) ? agent.Skills : [];
    }

    private static string UnknownText(string name) => string.Concat("Unknown skill '", name, UnknownTail);
}
