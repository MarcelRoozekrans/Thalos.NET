using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>
/// The <c>memory</c> tool source (<c>memory__remember/recall/forget/list</c>) built on <see cref="LocalToolSource"/>; returns no tools when
/// <see cref="MemoryOptions.Enabled"/> or <see cref="MemoryOptions.ExposeTools"/> is false. The tools are host-wide: an agent that opts into
/// auto-recall via <see cref="AgentMemorySettings.Enabled"/> while memory is globally disabled still gets no tools; which agents see them
/// is governed by <see cref="AgentDefinition.Tools"/> globs.
/// </summary>
public sealed class MemoryToolSource : IToolSource
{
    /// <summary>The source name; tools are qualified as <c>memory__{tool}</c>.</summary>
    public const string SourceName = "memory";

    private readonly LocalToolSource _inner;
    private readonly IOptions<MemoryOptions> _options;

    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public MemoryToolSource(IServiceProvider services, IOptions<MemoryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new LocalToolSource(SourceName, services, [typeof(MemoryTools)]);
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
