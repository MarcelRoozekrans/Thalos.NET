using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>
/// The <c>skills</c> tool source (<c>skills__load</c>, <c>skills__search</c>) built on <see cref="LocalToolSource"/>; returns no
/// tools when <see cref="SkillOptions.Enabled"/> or <see cref="SkillOptions.ExposeTools"/> is false. The tools are host-wide —
/// which agents see them is governed by <see cref="AgentDefinition.Tools"/> globs, and what they can load by
/// <see cref="AgentDefinition.Skills"/> globs. An agent with no skills keeps the tools and is told every name is unknown, which
/// is simpler than removing tools per agent.
/// </summary>
public sealed class SkillToolSource : IToolSource
{
    /// <summary>The source name; tools are qualified as <c>skills__{tool}</c>.</summary>
    public const string SourceName = "skills";

    private readonly LocalToolSource _inner;
    private readonly IOptions<SkillOptions> _options;

    /// <summary>Resolved by DI (<c>UseSkills</c>); <see cref="SkillTools"/> instances are created per invocation from <paramref name="services"/>.</summary>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public SkillToolSource(IServiceProvider services, IOptions<SkillOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new LocalToolSource(SourceName, services, [typeof(SkillTools)]);
        _options = options;
    }

    /// <inheritdoc />
    public string Name => SourceName;

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct) =>
        _options.Value is { Enabled: true, ExposeTools: true }
            ? _inner.GetToolsAsync(ct)
            : new(Result<IReadOnlyList<AITool>, AgentError>.Success([]));
}
