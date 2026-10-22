using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>
/// The <c>memory</c> tool source (<c>memory__remember/recall/forget/list</c>) built by <see cref="LocalToolSources.Create{TTools}"/>, without reflection; returns no tools when
/// <see cref="MemoryOptions.Enabled"/> or <see cref="MemoryOptions.ExposeTools"/> is false. The tools are host-wide: an agent that opts into
/// auto-recall via <see cref="AgentMemorySettings.Enabled"/> while memory is globally disabled still gets no tools; which agents see them
/// is governed by <see cref="AgentDefinition.Tools"/> globs.
/// </summary>
public sealed class MemoryToolSource : IToolSource
{
    /// <summary>The source name; tools are qualified as <c>memory__{tool}</c>.</summary>
    public const string SourceName = "memory";

    /// <summary>The tools' options: <see cref="MemoryToolsJsonContext"/>'s metadata with the settings a reflected tool's results are written with.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = LocalToolSources.CreateOptions(MemoryToolsJsonContext.Default);

    private readonly IToolSource _inner;
    private readonly IOptions<MemoryOptions> _options;

    /// <summary>Resolved by DI (<c>UseMemory</c>); <see cref="MemoryTools"/> instances are created per invocation from <paramref name="services"/>.</summary>
    public MemoryToolSource(IServiceProvider services, IOptions<MemoryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = LocalToolSources.Create<MemoryTools>(
            SourceName, services, SerializerOptions, static t => t.RememberAsync, static t => t.RecallAsync, static t => t.ForgetAsync, static t => t.ListAsync);
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
