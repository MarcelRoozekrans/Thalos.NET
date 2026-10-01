using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Thalos.Workspaces;

/// <summary>Registers the <c>workspace__*</c> file tools on a <see cref="ThalosBuilder"/>.</summary>
public static class WorkspaceToolsThalosBuilderExtensions
{
    /// <summary>
    /// Enables <c>workspace__read_file</c>, <c>workspace__list_files</c>, <c>workspace__write_file</c> and
    /// <c>workspace__edit_file</c>, confined to the calling run's workspace (<see cref="IRunWorkspaceProvider"/>).
    /// A caller with no run claim is refused. No grant check lives here: the host binds
    /// <c>workspace__write_*</c> and <c>workspace__edit_*</c> to a policy in <c>ToolPolicies</c>.
    /// </summary>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="allowedWriteExtensions">
    /// The host-wide ceiling of writable file extensions (ruling R29). Required, with no default: absence is not a
    /// supported configuration. An empty set refuses every write.
    /// </param>
    /// <param name="configure">
    /// Customises the rest of <see cref="RunWorkspaceToolOptions"/>. <see langword="null"/> keeps the defaults: no
    /// protected paths, a 256 KiB read cap, 500 listed entries and a 5-second contention timeout.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="configure"/> left a value that cannot work, such as a negative
    /// <see cref="RunWorkspaceToolOptions.ContentionTimeout"/> other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static ThalosBuilder UseRunWorkspaceTools(
        this ThalosBuilder builder, IReadOnlySet<string> allowedWriteExtensions, Action<RunWorkspaceToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(allowedWriteExtensions);

        var options = new RunWorkspaceToolOptions { AllowedWriteExtensions = allowedWriteExtensions };
        configure?.Invoke(options);
        options.Validate(nameof(configure));

        builder.Services.AddSingleton(options);
        return builder.AddLocalTools(RunWorkspaceToolOptions.SourceName, typeof(WorkspaceTools));
    }

    /// <summary>
    /// Enables the <c>workspace__*</c> file tools with every option set by <paramref name="configure"/>, including
    /// <see cref="RunWorkspaceToolOptions.AllowAnyWriteExtension"/>. The ceiling starts empty.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="configure"/> left a value that cannot work.</exception>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static ThalosBuilder UseRunWorkspaceTools(this ThalosBuilder builder, Action<RunWorkspaceToolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RunWorkspaceToolOptions { AllowedWriteExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        configure(options);
        options.Validate(nameof(configure));
        builder.Services.AddSingleton(options);
        return builder.AddLocalTools(RunWorkspaceToolOptions.SourceName, typeof(WorkspaceTools));
    }
}
