using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
    /// <see cref="ThalosToolAttribute"/> method declares a parameter whose model-deserializable shape — as
    /// System.Text.Json's own metadata for it describes that shape, never a hand-written guess at it — contains a
    /// type assignable to <see cref="ISecurityContext"/> anywhere other than the parameter's own top-level,
    /// by-value type. See <see cref="DescribeSecurityContextShapeViolation"/> for what "shape" covers.
    /// </exception>
    public LocalToolSource(string name, IServiceProvider services, IReadOnlyList<Type> toolTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(toolTypes);

        // One shape walk per construction call: a type shared by several parameters, or several tool methods, in
        // this batch is walked once, not once per reference to it — see FindSecurityContextPath.
        var walk = new ShapeWalk();
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
                    if (DescribeSecurityContextShapeViolation(type, method, parameter, walk) is { } violation)
                    {
                        throw new ArgumentException(violation, nameof(toolTypes));
                    }
                }
            }
        }

        Name = name;
        _services = services;
        _toolTypes = toolTypes;
        ShapeTypeInfoLookups = walk.TypeInfoLookups;
    }

    /// <summary>
    /// How many times this construction's shape walk asked System.Text.Json for a <see cref="JsonTypeInfo"/>. With
    /// the walk memoised per <see cref="Type"/>, that is at most the number of distinct types reachable from the
    /// tool parameters; without the memo it grows with the number of paths through a DTO graph that shares types.
    /// Exposed so a test can assert the walk's cost deterministically rather than by wall-clock time.
    /// </summary>
    internal int ShapeTypeInfoLookups { get; }

    /// <summary>
    /// Describes why a <see cref="ThalosToolAttribute"/> parameter's model-deserializable shape contains a type
    /// assignable to <see cref="ISecurityContext"/> anywhere other than the parameter's own top-level, by-value
    /// type, or null when the parameter is clean. <see cref="Options"/> binds the caller only when a parameter's
    /// own type is exactly <see cref="ISecurityContext"/> passed by value, so that is the one shape exempted here
    /// before ever consulting <see cref="AIJsonUtilities.DefaultOptions"/>: every other shape falls through to
    /// ordinary model-supplied binding instead, exposing the principal, or the part of the shape that carries it,
    /// in the JSON schema and accepting a model-forged value. A <c>ref</c>/<c>in</c>/<c>out</c> parameter has no
    /// JSON type info of its own — <see cref="Type.IsByRef"/> types cannot be passed to
    /// <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> — so it is always validated (and, for a principal,
    /// rejected) against its referenced element type instead, never exempted even when that element type is
    /// exactly <see cref="ISecurityContext"/>: MAF's reflection-based function factory cannot bind a by-ref
    /// parameter at all today, so it would otherwise only fail later and less clearly, inside
    /// <see cref="AIFunctionFactory.Create(MethodInfo, object?, AIFunctionFactoryOptions?)"/>. The caller throws
    /// the returned message as an <see cref="ArgumentException"/> against its own <c>toolTypes</c> parameter, so
    /// CA2208 sees a paramName that actually belongs to the throwing method.
    /// </summary>
    private static string? DescribeSecurityContextShapeViolation(Type toolType, MethodInfo method, ParameterInfo parameter, ShapeWalk walk)
    {
        var isByRef = parameter.ParameterType.IsByRef;
        var declaredType = isByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
        var effectiveType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (!isByRef && effectiveType == typeof(ISecurityContext))
        {
            return null; // the one bound shape: exactly ISecurityContext, passed by value
        }

        var offendingPath = FindSecurityContextPath(effectiveType, walk);
        if (offendingPath is null)
        {
            return null;
        }

        return $"Method '{toolType.FullName}.{method.Name}' parameter '{parameter.Name}' has a shape that contains a "
            + $"type assignable to ISecurityContext at '{parameter.Name}{offendingPath}'. Only a parameter whose own "
            + "declared type is exactly ISecurityContext, passed by value, is bound to the turn; declare it that "
            + "way, or remove ISecurityContext from this parameter's shape.";
    }

    /// <summary>
    /// Finds the first type assignable to <see cref="ISecurityContext"/> reachable from <paramref name="type"/>
    /// through <see cref="AIJsonUtilities.DefaultOptions"/>'s own <see cref="JsonTypeInfo"/> for it — the exact
    /// <see cref="JsonSerializerOptions"/> <see cref="Options"/> leaves
    /// <see cref="AIFunctionFactoryOptions.SerializerOptions"/> to default to, and so the same one a model's
    /// arguments are actually deserialized through. Walking that metadata, rather than reflecting over
    /// <paramref name="type"/> by hand, already reflects <c>[JsonInclude]</c> fields and non-public members,
    /// inherited members, a closed generic DTO's own properties, a collection subclass's element type (through
    /// <see cref="JsonTypeInfo.ElementType"/>, not its own declared properties, which is how a
    /// <c>class PList : List&lt;P&gt;</c> is caught even though <c>PList</c> is not itself generic), a
    /// dictionary's key and value types, and <c>[JsonDerivedType]</c> polymorphism, with no separate case needed
    /// for any of them.
    /// </summary>
    /// <returns>
    /// The path to the first match, relative to the caller's own path prefix, built from
    /// <see cref="JsonPropertyInfo.Name"/> — the JSON name a model-supplied value is actually keyed under, which
    /// can differ from the CLR member name under a naming policy or <c>[JsonPropertyName]</c>, and is what a
    /// developer reading this error would need to search the schema for — e.g. <c>".who"</c> for a property,
    /// <c>"[]"</c> for a collection or dictionary element, <c>"{key}"</c> for a dictionary key, or
    /// <c>":TypeName"</c> for a polymorphic derived type; an empty string when <paramref name="type"/> itself
    /// matches; null when no match exists anywhere in the shape.
    /// </returns>
    /// <param name="type">The type to walk.</param>
    /// <param name="walk">The memo, recursion stack and lookup count shared by one construction; see <see cref="ShapeWalk"/>.</param>
    private static string? FindSecurityContextPath(Type type, ShapeWalk walk)
    {
        if (walk.Memo.TryGetValue(type, out var cached))
        {
            return cached;
        }

        if (!walk.Visiting.Add(type))
        {
            return null;
        }

        try
        {
            var path = ComputeSecurityContextPath(type, walk);
            walk.Memo[type] = path;
            return path;
        }
        finally
        {
            walk.Visiting.Remove(type);
        }
    }

    private static string? ComputeSecurityContextPath(Type type, ShapeWalk walk)
    {
        if (typeof(ISecurityContext).IsAssignableFrom(type))
        {
            return string.Empty;
        }

        walk.TypeInfoLookups++;
        var info = AIJsonUtilities.DefaultOptions.GetTypeInfo(type);

        foreach (var property in info.Properties)
        {
            if (FindSecurityContextPath(property.PropertyType, walk) is { } propertyPath)
            {
                return "." + property.Name + propertyPath;
            }
        }

        if (info.ElementType is { } elementType && FindSecurityContextPath(elementType, walk) is { } elementPath)
        {
            return "[]" + elementPath;
        }

        if (info.KeyType is { } keyType && FindSecurityContextPath(keyType, walk) is { } keyPath)
        {
            return "{key}" + keyPath;
        }

        if (info.PolymorphismOptions is { } polymorphism)
        {
            foreach (var derived in polymorphism.DerivedTypes)
            {
                if (FindSecurityContextPath(derived.DerivedType, walk) is { } derivedPath)
                {
                    return ":" + derived.DerivedType.Name + derivedPath;
                }
            }
        }

        return null;
    }

    /// <summary>State for one construction's shape walk; never shared across constructions, so never across threads.</summary>
    private sealed class ShapeWalk
    {
        /// <summary>
        /// The final result per <see cref="Type"/>, so a type reachable from many parameters, or repeated at several
        /// depths of the same DTO graph, is walked once. Without it the walk is exponential in a graph that shares
        /// types.
        /// </summary>
        public Dictionary<Type, string?> Memo { get; } = [];

        /// <summary>
        /// The types currently on the recursion stack. Re-entering one of them, a cycle in the DTO graph, counts as
        /// clean for that path and is not written to <see cref="Memo"/>, since "clean because it's a cycle" is not
        /// that type's final, cacheable answer.
        /// </summary>
        public HashSet<Type> Visiting { get; } = [];

        /// <summary>How many <see cref="JsonTypeInfo"/> lookups the walk made; surfaced as <see cref="ShapeTypeInfoLookups"/>.</summary>
        public int TypeInfoLookups { get; set; }
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
    /// The match is exact-type, not assignability: <see cref="DescribeSecurityContextShapeViolation"/> rejects, eagerly at
    /// construction, any parameter whose System.Text.Json shape carries an <see cref="ISecurityContext"/>-assignable
    /// type anywhere other than this one exact, by-value spot, because the ambient caller cannot be guaranteed to be
    /// that other concrete type. Some shapes are left alone deliberately, as a documented, free-form limitation:
    /// <see cref="object"/>, <see cref="System.Text.Json.JsonElement"/> and
    /// <see cref="System.Text.Json.Nodes.JsonNode"/>, and any type or member serialized through a custom
    /// <see cref="System.Text.Json.Serialization.JsonConverter"/>. None of them carries a static shape in
    /// System.Text.Json's metadata for the shape walk to inspect: the first three are containers for arbitrary JSON,
    /// and a converter decides for itself what it builds from the JSON it reads, so a converter written to build a
    /// principal passes the walk. Each is indistinguishable from a tool that legitimately wants free-form model input,
    /// so they are bound and schema'd exactly like any other parameter. A tool author who declares one of them owns
    /// what it deserializes to; the only principal the turn vouches for is a parameter typed exactly
    /// <see cref="ISecurityContext"/>.
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
