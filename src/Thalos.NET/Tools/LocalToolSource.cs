using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>
/// In-process tools discovered from <see cref="ThalosToolTypeAttribute"/> classes. Each invocation runs in a
/// fresh DI scope so scoped dependencies (DbContexts, repositories) are never stale; the scope's provider is also
/// exposed to the tool via <see cref="AIFunctionArguments.Services"/>.
/// </summary>
[RequiresUnreferencedCode("Discovers tool methods via reflection.")]
[RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
public sealed class LocalToolSource : IToolSource
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<Type> _toolTypes;
    private IReadOnlyList<AITool>? _tools;

    /// <summary>Creates a source named <paramref name="name"/> over <paramref name="toolTypes"/>; method discovery is deferred to the first <see cref="GetToolsAsync"/>.</summary>
    /// <exception cref="ArgumentException">A type in <paramref name="toolTypes"/> is not marked <see cref="ThalosToolTypeAttribute"/>.</exception>
    public LocalToolSource(string name, IServiceProvider services, IReadOnlyList<Type> toolTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(toolTypes);
        foreach (var type in toolTypes)
        {
            if (!type.IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
            {
                throw new ArgumentException($"Type '{type.FullName}' is not marked [ThalosToolType].", nameof(toolTypes));
            }
        }

        Name = name;
        _services = services;
        _toolTypes = toolTypes;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        _tools ??= Discover();
        return new(Result<IReadOnlyList<AITool>, AgentError>.Success(_tools));
    }

    private List<AITool> Discover()
    {
        var tools = new List<AITool>();
        using var probeScope = _services.CreateScope();

        foreach (var type in _toolTypes)
        {
            var probe = ActivatorUtilities.CreateInstance(probeScope.ServiceProvider, type);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<ThalosToolAttribute>() is not { } attr)
                {
                    continue;
                }

                var toolName = attr.Name ?? method.Name;
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var probeFunction = AIFunctionFactory.Create(method, method.IsStatic ? null : probe, new AIFunctionFactoryOptions { Name = toolName, Description = description });
                tools.Add(method.IsStatic ? probeFunction : new ScopedTool(_services, type, method, probeFunction));
            }
        }

        return tools;
    }

    /// <summary>Metadata from the probe function; a fresh scope + instance per invocation, disposed after the call.</summary>
    private sealed class ScopedTool(IServiceProvider root, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type toolType, MethodInfo method, AIFunction probe)
        : DelegatingAIFunction(probe)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var scope = root.CreateAsyncScope();
            await using var _ = scope.ConfigureAwait(false);
            var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, toolType);
            try
            {
                var bound = AIFunctionFactory.Create(method, instance, new AIFunctionFactoryOptions { Name = Name, Description = Description });
                var scopedArguments = new AIFunctionArguments(arguments, StringComparer.Ordinal) { Services = scope.ServiceProvider, Context = arguments.Context };
                return await bound.InvokeAsync(scopedArguments, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                switch (instance)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
    }
}
