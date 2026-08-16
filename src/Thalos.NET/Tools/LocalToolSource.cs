using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>
/// In-process tools discovered from <see cref="ThalosToolTypeAttribute"/> classes. Each invocation runs in a
/// fresh DI scope so scoped dependencies (DbContexts, repositories) are never stale.
/// </summary>
[RequiresUnreferencedCode("Discovers tool methods via reflection.")]
public sealed class LocalToolSource(string name, IServiceProvider services, IReadOnlyList<Type> toolTypes) : IToolSource
{
    private IReadOnlyList<AITool>? _tools;

    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        _tools ??= Discover();
        return new(Result<IReadOnlyList<AITool>, AgentError>.Success(_tools));
    }

    private List<AITool> Discover()
    {
        var tools = new List<AITool>();
        using var probeScope = services.CreateScope();

        foreach (var type in toolTypes)
        {
            if (!type.IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
            {
                throw new ArgumentException($"Type '{type.FullName}' is not marked [ThalosToolType].", nameof(toolTypes));
            }

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
                tools.Add(method.IsStatic ? probeFunction : new ScopedTool(services, type, method, probeFunction));
            }
        }

        return tools;
    }

    /// <summary>Metadata from the probe function; a fresh scope + instance per invocation.</summary>
    private sealed class ScopedTool(IServiceProvider root, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type toolType, MethodInfo method, AIFunction probe)
        : DelegatingAIFunction(probe)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var scope = root.CreateAsyncScope();
            await using var _ = scope.ConfigureAwait(false);
            var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, toolType);
            var bound = AIFunctionFactory.Create(method, instance, new AIFunctionFactoryOptions { Name = Name, Description = Description });
            return await bound.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
    }
}
