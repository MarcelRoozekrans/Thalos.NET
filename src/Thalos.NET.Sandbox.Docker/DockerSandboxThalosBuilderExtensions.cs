using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Thalos.Sandbox.Docker;

/// <summary>Registers <see cref="DockerSandboxRuntime"/> on a <see cref="ThalosBuilder"/>.</summary>
public static class DockerSandboxThalosBuilderExtensions
{
    /// <summary>
    /// Uses <see cref="DockerSandboxRuntime"/> as the (singleton) <see cref="ISandboxRuntime"/>, replacing any earlier
    /// registration, and registers <see cref="TimeProvider.System"/> if nothing else already has.
    /// </summary>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="configure">Sets <see cref="DockerSandboxOptions"/>.</param>
    /// <exception cref="ArgumentException">The options are invalid once <paramref name="configure"/> has run, for example an egress domain overlaps another.</exception>
    public static ThalosBuilder UseDockerSandboxRuntime(this ThalosBuilder builder, Action<DockerSandboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new DockerSandboxOptions();
        configure?.Invoke(options);
        var valid = options.Validate();
        if (valid.IsFailure)
        {
            throw new ArgumentException($"Invalid DockerSandboxOptions: {valid.Error.Message}", nameof(configure));
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new DockerSandboxRuntime(
            options,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DockerSandboxRuntime>>())));
        builder.Services.Replace(ServiceDescriptor.Singleton<ISandboxRuntime>(sp => sp.GetRequiredService<DockerSandboxRuntime>()));
        return builder;
    }
}
