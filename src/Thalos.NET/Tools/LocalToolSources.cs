using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Thalos.Tools;

/// <summary>
/// Builds reflection-free tool sources outside <see cref="ThalosBuilder.AddLocalTools{TTools}"/>: for a package that
/// wraps the source in its own <see cref="IToolSource"/>, or builds it over a container of its own, and for the
/// <see cref="JsonSerializerOptions"/> such a source takes.
/// </summary>
public static class LocalToolSources
{
    /// <summary>
    /// The serializer options for a reflection-free tool source: <see cref="AIJsonUtilities.DefaultOptions"/>' settings,
    /// the ones a reflected tool's arguments and results go through, its encoder among them, with
    /// <paramref name="resolver"/> as the only resolver, read-only. A context's own <c>Options</c> has other settings, so
    /// its results would be escaped differently from a reflected tool's.
    /// </summary>
    /// <param name="resolver">The tools' metadata, typically a source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>.</param>
    /// <returns>Read-only options.</returns>
    /// <example><c>LocalToolSources.CreateOptions(MyJsonContext.Default)</c></example>
    public static JsonSerializerOptions CreateOptions(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// The tool source <see cref="ThalosBuilder.AddLocalTools{TTools}"/> registers, built directly: the tools of the
    /// <see cref="ThalosToolTypeAttribute"/> class <typeparamref name="TTools"/>, each named by a binder that returns the
    /// tool method bound to an instance, as a method group, with arguments and results going through
    /// <paramref name="serializerOptions"/>. Nothing reflects over <typeparamref name="TTools"/>' methods, so a trimmed or
    /// Native AOT host can use it.
    /// </summary>
    /// <typeparam name="TTools">The tool class; its public constructor is resolved from <paramref name="services"/> on every call.</typeparam>
    /// <param name="name">The source name.</param>
    /// <param name="services">The container each call's scope, and the probe instance, are created from.</param>
    /// <param name="serializerOptions">
    /// The options the tools' arguments and results go through, typically from <see cref="CreateOptions"/>. Their resolver
    /// must cover every parameter and result type.
    /// </param>
    /// <param name="tools">One binder per tool, such as <c>t =&gt; t.ReadFile</c>.</param>
    /// <returns>The source. Its tools are built on the first <see cref="IToolSource.GetToolsAsync"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/>, <paramref name="serializerOptions"/> or <paramref name="tools"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank or not a valid source name, <typeparamref name="TTools"/> is not marked
    /// <see cref="ThalosToolTypeAttribute"/>, or no binder is given.
    /// </exception>
    /// <remarks>
    /// The tools behave as <see cref="ThalosBuilder.AddLocalTools{TTools}"/>'s do. A binder that returns anything but one
    /// of <typeparamref name="TTools"/>'s own <see cref="ThalosToolAttribute"/> methods, or a parameter whose shape hides
    /// a principal, fails the source's first <see cref="IToolSource.GetToolsAsync"/>.
    /// </remarks>
    public static IToolSource Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTools>(
        string name, IServiceProvider services, JsonSerializerOptions serializerOptions, params Func<TTools, Delegate>[] tools)
        where TTools : class
    {
        ThrowIfInvalid(serializerOptions, tools);
        return new LocalToolSource<TTools>(name, services, serializerOptions, [.. tools]);
    }

    /// <summary>The checks <see cref="Create{TTools}"/> and <see cref="ThalosBuilder.AddLocalTools{TTools}"/> share, against their <c>tools</c> parameter.</summary>
    internal static void ThrowIfInvalid<TTools>(JsonSerializerOptions serializerOptions, Func<TTools, Delegate>[] tools)
        where TTools : class
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(tools);
        if (!typeof(TTools).IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
        {
            throw new ArgumentException($"Type '{typeof(TTools).FullName}' is not marked [ThalosToolType].", nameof(tools));
        }

        if (tools.Length == 0)
        {
            throw new ArgumentException("At least one tool binder is required.", nameof(tools));
        }
    }
}
