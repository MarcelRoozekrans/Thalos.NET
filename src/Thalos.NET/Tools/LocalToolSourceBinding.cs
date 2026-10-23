using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Tools;

/// <summary>
/// What <see cref="LocalToolSource"/> and the reflection-free <see cref="LocalToolSource{TTools}"/> share: the
/// <see cref="AIFunctionFactoryOptions"/> that bind the caller, and the construction-time guard against a principal
/// hidden in a parameter's shape. Neither reflects over a tool type; both take the <see cref="JsonSerializerOptions"/>
/// the tool's arguments are deserialized through.
/// </summary>
internal static class LocalToolSourceBinding
{
    /// <summary>
    /// Describes why a <see cref="ThalosToolAttribute"/> parameter's model-deserializable shape contains a type
    /// assignable to <see cref="ISecurityContext"/> anywhere other than the parameter's own top-level, by-value
    /// type, or null when the parameter is clean. <see cref="Options"/> binds the caller only when a parameter's
    /// own type is exactly <see cref="ISecurityContext"/> passed by value, so that is the one shape exempted here
    /// before ever consulting <paramref name="serializerOptions"/>: every other shape falls through to
    /// ordinary model-supplied binding instead, exposing the principal, or the part of the shape that carries it,
    /// in the JSON schema and accepting a model-forged value. A <c>ref</c>/<c>in</c>/<c>out</c> parameter has no
    /// JSON type info of its own — <see cref="Type.IsByRef"/> types cannot be passed to
    /// <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> — so it is always validated (and, for a principal,
    /// rejected) against its referenced element type instead, never exempted even when that element type is
    /// exactly <see cref="ISecurityContext"/>: MAF's function factory cannot bind a by-ref parameter at all today,
    /// so it would otherwise only fail later and less clearly, inside <see cref="AIFunctionFactory"/>. The caller
    /// throws the returned message as an <see cref="ArgumentException"/> against its own parameter, so CA2208 sees a
    /// paramName that actually belongs to the throwing method.
    /// </summary>
    /// <param name="toolType">The tool class, for the message.</param>
    /// <param name="method">The tool method, for the message.</param>
    /// <param name="parameter">The parameter checked.</param>
    /// <param name="walk">The memo shared by one construction.</param>
    /// <param name="serializerOptions">The options the tool's arguments are deserialized through; their metadata is the shape walked.</param>
    public static string? DescribeSecurityContextShapeViolation(Type toolType, MethodInfo method, ParameterInfo parameter, ShapeWalk walk, JsonSerializerOptions serializerOptions)
    {
        var isByRef = parameter.ParameterType.IsByRef;
        var declaredType = isByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
        var effectiveType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (!isByRef && effectiveType == typeof(ISecurityContext))
        {
            return null; // the one bound shape: exactly ISecurityContext, passed by value
        }

        if (!isByRef && IsBoundByFactory(effectiveType))
        {
            return null; // never read from the model's arguments, so it has no JSON shape to walk
        }

        var offendingPath = FindSecurityContextPath(effectiveType, walk, serializerOptions);
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
    /// Runs one call of an instance tool method in a fresh DI scope: creates the instance from the scope, binds the
    /// function to it, invokes it with the scope as <see cref="AIFunctionArguments.Services"/>, and disposes the instance,
    /// then the scope.
    /// </summary>
    /// <param name="root">The container the scope is created from.</param>
    /// <param name="create">Creates the tool instance from the scope's provider.</param>
    /// <param name="bind">The function bound to that instance.</param>
    /// <param name="arguments">The call's arguments; copied, with the scope's provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async ValueTask<object?> InvokeScopedAsync<TInstance>(
        IServiceProvider root, Func<IServiceProvider, TInstance> create, Func<TInstance, AIFunction> bind, AIFunctionArguments arguments, CancellationToken cancellationToken)
        where TInstance : notnull
    {
        var scope = root.CreateAsyncScope();
        await using var _ = scope.ConfigureAwait(false);
        var instance = create(scope.ServiceProvider);
        try
        {
            var scopedArguments = new AIFunctionArguments(arguments, StringComparer.Ordinal) { Services = scope.ServiceProvider, Context = arguments.Context };
            return await bind(instance).InvokeAsync(scopedArguments, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The parameter types <see cref="AIFunctionFactory"/> binds from the call itself, never from the model's JSON:
    /// <see cref="CancellationToken"/>, <see cref="IServiceProvider"/> from <see cref="AIFunctionArguments.Services"/>, and
    /// the <see cref="AIFunctionArguments"/>. Source-generated options have no metadata for them, and need none.
    /// </summary>
    private static bool IsBoundByFactory(Type type) =>
        type == typeof(CancellationToken) || type == typeof(IServiceProvider) || type == typeof(AIFunctionArguments);

    /// <summary>
    /// Options shared by the probe and bound <see cref="AIFunctionFactory"/> calls for a tool method. A parameter
    /// typed exactly <see cref="ISecurityContext"/> is bound to the calling turn's <see cref="TurnScope.Caller"/>,
    /// falling back to <see cref="AnonymousSecurityContext.Instance"/> outside a turn, and is excluded from the JSON
    /// schema so the model can never see or forge it.
    /// </summary>
    /// <remarks>
    /// The match is exact-type, not assignability: <see cref="DescribeSecurityContextShapeViolation"/> rejects, eagerly at
    /// construction, any parameter whose System.Text.Json shape carries an <see cref="ISecurityContext"/>-assignable
    /// type anywhere other than this one exact, by-value spot, because the ambient caller cannot be guaranteed to be
    /// that other concrete type. Some shapes are left alone deliberately, as a documented, free-form limitation:
    /// <see cref="object"/>, <see cref="JsonElement"/> and <see cref="System.Text.Json.Nodes.JsonNode"/>, and any
    /// type or member serialized through a custom <see cref="System.Text.Json.Serialization.JsonConverter"/>. None of
    /// them carries a static shape in System.Text.Json's metadata for the shape walk to inspect: the first three are
    /// containers for arbitrary JSON, and a converter decides for itself what it builds from the JSON it reads, so a
    /// converter written to build a principal passes the walk. Each is indistinguishable from a tool that legitimately
    /// wants free-form model input, so they are bound and schema'd exactly like any other parameter. A tool author who
    /// declares one of them owns what it deserializes to; the only principal the turn vouches for is a parameter typed
    /// exactly <see cref="ISecurityContext"/>.
    /// </remarks>
    /// <param name="name">The tool's name.</param>
    /// <param name="description">The tool's description, or null for the method's own.</param>
    /// <param name="serializerOptions">The options arguments and results go through; null for <see cref="AIJsonUtilities.DefaultOptions"/>.</param>
    public static AIFunctionFactoryOptions Options(string name, string? description, JsonSerializerOptions? serializerOptions) => new()
    {
        Name = name,
        Description = description,
        SerializerOptions = serializerOptions,
        ConfigureParameterBinding = static p => p.ParameterType == typeof(ISecurityContext)
            ? new AIFunctionFactoryOptions.ParameterBindingOptions
            {
                ExcludeFromSchema = true,
                BindParameter = static (_, _) => TurnScope.Current?.Caller ?? AnonymousSecurityContext.Instance,
            }
            : default,
    };

    /// <summary>
    /// Finds the first type assignable to <see cref="ISecurityContext"/> reachable from <paramref name="type"/>
    /// through <paramref name="serializerOptions"/>' own <see cref="JsonTypeInfo"/> for it — the options a model's
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
    /// <param name="serializerOptions">The options whose metadata is walked.</param>
    private static string? FindSecurityContextPath(Type type, ShapeWalk walk, JsonSerializerOptions serializerOptions)
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
            var path = ComputeSecurityContextPath(type, walk, serializerOptions);
            walk.Memo[type] = path;
            return path;
        }
        finally
        {
            walk.Visiting.Remove(type);
        }
    }

    private static string? ComputeSecurityContextPath(Type type, ShapeWalk walk, JsonSerializerOptions serializerOptions)
    {
        if (typeof(ISecurityContext).IsAssignableFrom(type))
        {
            return string.Empty;
        }

        walk.TypeInfoLookups++;
        var info = serializerOptions.GetTypeInfo(type);

        foreach (var property in info.Properties)
        {
            if (FindSecurityContextPath(property.PropertyType, walk, serializerOptions) is { } propertyPath)
            {
                return "." + property.Name + propertyPath;
            }
        }

        if (info.ElementType is { } elementType && FindSecurityContextPath(elementType, walk, serializerOptions) is { } elementPath)
        {
            return "[]" + elementPath;
        }

        if (info.KeyType is { } keyType && FindSecurityContextPath(keyType, walk, serializerOptions) is { } keyPath)
        {
            return "{key}" + keyPath;
        }

        if (info.PolymorphismOptions is { } polymorphism)
        {
            foreach (var derived in polymorphism.DerivedTypes)
            {
                if (FindSecurityContextPath(derived.DerivedType, walk, serializerOptions) is { } derivedPath)
                {
                    return ":" + derived.DerivedType.Name + derivedPath;
                }
            }
        }

        return null;
    }

    /// <summary>State for one construction's shape walk; never shared across constructions, so never across threads.</summary>
    internal sealed class ShapeWalk
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

        /// <summary>How many <see cref="JsonTypeInfo"/> lookups the walk made.</summary>
        public int TypeInfoLookups { get; set; }
    }
}
