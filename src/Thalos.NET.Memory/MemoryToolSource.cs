using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>The <c>memory</c> tool source (<c>memory__remember/recall/forget/list</c>) built on <see cref="LocalToolSource"/>; returns no tools when memory or <see cref="MemoryOptions.ExposeTools"/> is disabled.</summary>
public sealed class MemoryToolSource : IToolSource
{
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
