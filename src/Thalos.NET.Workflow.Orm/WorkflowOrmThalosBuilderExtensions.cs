using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Thalos.Workflow.Orm;

/// <summary>Registers the ZeroAlloc.ORM-backed <see cref="IWorkflowStore"/> on a <see cref="ThalosBuilder"/>.</summary>
/// <remarks>
/// The design brief for this task named the extension target <c>IThalosBuilder</c>, but Thalos.NET's actual
/// composition-root type — the one every other integration package (<c>Thalos.NET.Git.LibGit2Sharp</c>,
/// <c>Thalos.NET.Memory.RagNet</c>, <c>Thalos.NET.Channels.Telegram</c>, ...) extends — is the concrete sealed
/// <see cref="ThalosBuilder"/> class in <c>Thalos.NET</c>. This follows that existing, real convention instead.
/// </remarks>
public static class WorkflowOrmThalosBuilderExtensions
{
    /// <summary>
    /// Uses <see cref="OrmWorkflowStore"/> as the <see cref="IWorkflowStore"/>, replacing any earlier
    /// registration. When <see cref="WorkflowOrmOptions.EnsureSchemaOnStartup"/> is set (the default), also
    /// registers a hosted service that applies the outbox and workflow migrations at startup.
    /// </summary>
    public static ThalosBuilder AddWorkflowOrm(this ThalosBuilder builder, Action<WorkflowOrmOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WorkflowOrmOptions { ConnectionString = "" };
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("WorkflowOrmOptions.ConnectionString must be set.", nameof(configure));
        }

        var services = builder.Services;
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStore>(sp => new OrmWorkflowStore(sp.GetRequiredService<WorkflowOrmOptions>())));

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorkflowOrmSchemaInitializer))
            {
                services.RemoveAt(i);
            }
        }

        if (options.EnsureSchemaOnStartup)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowOrmSchemaInitializer>());
        }

        return builder;
    }
}
