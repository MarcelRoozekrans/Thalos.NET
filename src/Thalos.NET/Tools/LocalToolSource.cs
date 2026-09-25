using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Runtime;
using ZeroAlloc.Authorization;
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

    private const BindingFlags ToolMethodFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Creates a source named <paramref name="name"/> over <paramref name="toolTypes"/>; method discovery is deferred to the first <see cref="GetToolsAsync"/>.</summary>
    /// <exception cref="ArgumentException">
    /// A type in <paramref name="toolTypes"/> is not marked <see cref="ThalosToolTypeAttribute"/>, or a
    /// <see cref="ThalosToolAttribute"/> method declares a parameter whose type is assignable to
    /// <see cref="ISecurityContext"/> but is not exactly <see cref="ISecurityContext"/> itself. <see cref="Options"/>
    /// binds the caller only on an exact type match, so a concrete principal type or a derived interface would fall
    /// through to ordinary model-supplied binding: it would appear in the JSON schema, and a model-forged value would
    /// reach the tool (or, for an interface the reflection marshaller cannot construct, throw at call time).
    /// </exception>
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

            foreach (var method in type.GetMethods(ToolMethodFlags))
            {
                if (!Attribute.IsDefined(method, typeof(ThalosToolAttribute)))
                {
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (typeof(ISecurityContext).IsAssignableFrom(parameter.ParameterType) && parameter.ParameterType != typeof(ISecurityContext))
                    {
                        throw new ArgumentException(
                            $"Method '{type.FullName}.{method.Name}' parameter '{parameter.Name}' is typed '{parameter.ParameterType.FullName}', "
                            + "which is assignable to ISecurityContext but is not exactly ISecurityContext. The caller is bound only on an exact "
                            + "type match; declare the parameter as ISecurityContext itself.",
                            nameof(toolTypes));
                    }
                }
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
            foreach (var method in type.GetMethods(ToolMethodFlags))
            {
                if (method.GetCustomAttribute<ThalosToolAttribute>() is not { } attr)
                {
                    continue;
                }

                var toolName = attr.Name ?? method.Name;
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var probeFunction = AIFunctionFactory.Create(method, method.IsStatic ? null : probe, Options(toolName, description));
                tools.Add(method.IsStatic ? probeFunction : new ScopedTool(_services, type, method, probeFunction));
            }
        }

        return tools;
    }

    /// <summary>
    /// Options shared by the probe and bound <see cref="AIFunctionFactory.Create(MethodInfo, object?, AIFunctionFactoryOptions?)"/>
    /// calls for a tool method. A parameter typed exactly <see cref="ISecurityContext"/> is bound to the calling
    /// turn's <see cref="TurnScope.Caller"/>, falling back to <see cref="AnonymousSecurityContext.Instance"/> outside
    /// a turn, and is excluded from the JSON schema so the model can never see or forge it.
    /// </summary>
    /// <remarks>
    /// The match is exact-type, not assignability: the constructor rejects any concrete principal type or derived
    /// <see cref="ISecurityContext"/> interface eagerly, because the ambient caller cannot be guaranteed to be that
    /// concrete type. A parameter typed as the non-specific <see cref="object"/> is not, and cannot be, treated
    /// specially here — nothing distinguishes it from a tool that legitimately wants free-form model input, so it is
    /// bound and schema'd exactly like any other <see cref="object"/> parameter.
    /// </remarks>
    private static AIFunctionFactoryOptions Options(string name, string? description) => new()
    {
        Name = name,
        Description = description,
        ConfigureParameterBinding = static p => p.ParameterType == typeof(ISecurityContext)
            ? new AIFunctionFactoryOptions.ParameterBindingOptions
            {
                ExcludeFromSchema = true,
                BindParameter = static (_, _) => TurnScope.Current?.Caller ?? AnonymousSecurityContext.Instance,
            }
            : default,
    };

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
                var bound = AIFunctionFactory.Create(method, instance, Options(Name, Description));
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
