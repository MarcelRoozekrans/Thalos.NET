using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>
/// Collects several <see cref="ThalosToolTypeAttribute"/> classes for one local tool source, for
/// <see cref="ThalosBuilder.AddLocalTools(string, JsonSerializerOptions, Action{LocalToolsBuilder})"/>. Each class is
/// added as <see cref="ThalosBuilder.AddLocalTools{TTools}"/> adds one: by binders, without reflection, so a trimmed or
/// Native AOT host can use it.
/// </summary>
/// <example>
/// <code>
/// thalos.AddLocalTools("knowledge", options, tools => tools
///     .Add&lt;SearchTools&gt;(t =&gt; t.Search)
///     .Add&lt;ScheduleTools&gt;(t =&gt; t.ListSchedules, t =&gt; t.WhyDidARunFail));
/// </code>
/// </example>
public sealed class LocalToolsBuilder
{
    private readonly string _sourceName;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly List<Func<IServiceProvider, IToolSource>> _parts = [];
    private readonly HashSet<Type> _types = [];

    internal LocalToolsBuilder(string sourceName, JsonSerializerOptions serializerOptions)
    {
        _sourceName = sourceName;
        _serializerOptions = serializerOptions;
    }

    /// <summary>The number of classes added so far.</summary>
    internal int Count => _parts.Count;

    /// <summary>
    /// Adds the tools of the <see cref="ThalosToolTypeAttribute"/> class <typeparamref name="TTools"/> that
    /// <paramref name="tools"/> name, to the source this builder configures.
    /// </summary>
    /// <typeparam name="TTools">The tool class; its public constructor is resolved from the container on every call.</typeparam>
    /// <param name="tools">One binder per tool, such as <c>t =&gt; t.ReadFile</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TTools"/> is not marked <see cref="ThalosToolTypeAttribute"/>, no binder is given, or
    /// <typeparamref name="TTools"/> was already added to this source.
    /// </exception>
    /// <remarks>
    /// A binder that returns anything but one of <typeparamref name="TTools"/>'s own <see cref="ThalosToolAttribute"/>
    /// methods, a parameter whose shape hides a principal, or a tool name another class of this source also uses, fails
    /// the source's first <see cref="IToolSource.GetToolsAsync"/>.
    /// </remarks>
    public LocalToolsBuilder Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTools>(
        params Func<TTools, Delegate>[] tools)
        where TTools : class
    {
        LocalToolSources.ThrowIfInvalid(_serializerOptions, tools);
        if (!_types.Add(typeof(TTools)))
        {
            throw new ArgumentException(
                $"Type '{typeof(TTools).FullName}' was already added to tool source '{_sourceName}'; give all its tools in one Add call.",
                nameof(tools));
        }

        Func<TTools, Delegate>[] binders = [.. tools];
        var name = _sourceName;
        var options = _serializerOptions;
        _parts.Add(sp => new LocalToolSource<TTools>(name, sp, options, binders));
        return this;
    }

    /// <summary>The one source over every added class, built in the container.</summary>
    /// <param name="services">The container.</param>
    internal IToolSource Build(IServiceProvider services) =>
        new CombinedLocalToolSource(_sourceName, [.. _parts.Select(part => part(services))]);

    /// <summary>One named source over several local sources of that name, their tools in the order the classes were added.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="parts">The per-class sources.</param>
    private sealed class CombinedLocalToolSource(string name, IReadOnlyList<IToolSource> parts) : IToolSource
    {
        private IReadOnlyList<AITool>? _tools;

        public string Name => name;

        public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
        {
            if (_tools is { } built)
            {
                return Result<IReadOnlyList<AITool>, AgentError>.Success(built);
            }

            var tools = new List<AITool>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                var partTools = await part.GetToolsAsync(ct).ConfigureAwait(false);
                if (partTools.IsFailure)
                {
                    return partTools;
                }

                foreach (var tool in partTools.Value)
                {
                    if (!names.Add(tool.Name))
                    {
                        throw new InvalidOperationException(
                            $"Tool '{tool.Name}' is defined by more than one class of tool source '{name}'.");
                    }

                    tools.Add(tool);
                }
            }

            _tools = tools;
            return Result<IReadOnlyList<AITool>, AgentError>.Success(tools);
        }
    }
}
