using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>
/// The default index when no <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> is registered: indexing is a
/// successful no-op (a host without embeddings still starts and still gets its catalogue) and search reports that it is
/// unavailable. The catalogue in the agent's instructions stays authoritative either way.
/// </summary>
public sealed class UnavailableSkillIndex : ISkillIndex
{
    /// <summary>The message <c>skills__search</c> turns into its "search is unavailable" answer.</summary>
    public const string Reason = "Skill search is unavailable: register an IEmbeddingGenerator<string, Embedding<float>> or a custom ISkillIndex with UseSkillIndex<T>().";

    /// <summary>The singleton (the type is stateless).</summary>
    public static UnavailableSkillIndex Instance { get; } = new();

    private UnavailableSkillIndex()
    {
    }

    /// <inheritdoc />
    /// <remarks>A successful no-op: a start-up sync must not fail because the host has no embedding generator.</remarks>
    public ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<SkillDocument> skills, CancellationToken ct) => new(UnitResult<AgentError>.Success());

    /// <inheritdoc />
    /// <remarks>Always <see cref="AgentErrorCode.SkillSearchUnavailable"/> — never an empty success, which would read as "no matching skills".</remarks>
    public ValueTask<Result<IReadOnlyList<SkillHit>, AgentError>> SearchAsync(string query, SkillSearchOptions options, CancellationToken ct) =>
        new(Result<IReadOnlyList<SkillHit>, AgentError>.Failure(AgentError.SkillSearchUnavailable(Reason)));

    /// <inheritdoc />
    /// <remarks>A successful no-op, like <see cref="UpsertAsync"/>.</remarks>
    public ValueTask<UnitResult<AgentError>> RemoveAsync(SkillName name, CancellationToken ct) => new(UnitResult<AgentError>.Success());
}
