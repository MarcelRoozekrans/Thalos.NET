using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Registers the ZeroAlloc.ORM-backed <see cref="IWorkflowStore"/> and <see cref="IProcessDefinitionStore"/> on a
/// <see cref="ThalosBuilder"/>. Both share the same <see cref="WorkflowOrmOptions"/> registration, and the
/// workflow store resolves process definitions through the very <see cref="IProcessDefinitionStore"/> registered
/// here, so syncing a definition is what makes it runnable. Both open a connection per call — one
/// <see cref="AddWorkflowOrm"/> call is enough to get everything <em>this package</em> offers; a consumer should
/// never need a second, hand-written registration for <see cref="IProcessDefinitionStore"/>.
/// </summary>
/// <remarks>
/// <b>This is two of the seven pieces a moving run needs, not all seven.</b> A host that stops here has a database
/// that records runs and nothing that advances them: no <see cref="IWorkflowReferenceResolver"/>, no
/// <c>WorkflowNodeDispatcher</c>, no outbox consumer bound to <see cref="WorkflowDispatch.TypeName"/>, no
/// <see cref="WorkflowRunReconciler"/> on a schedule, and no <see cref="IProcessDefinitionSource"/> feeding
/// <see cref="ProcessDefinitionSync"/>. Every one of those is host policy or host hosting, which is why none of
/// them is registered here — <c>docs/workflow.md</c> is the end-to-end wiring.
/// </remarks>
/// <remarks>
/// The design brief for this task named the extension target <c>IThalosBuilder</c>, but Thalos.NET's actual
/// composition-root type — the one every other integration package (<c>Thalos.NET.Git.LibGit2Sharp</c>,
/// <c>Thalos.NET.Memory.RagNet</c>, <c>Thalos.NET.Channels.Telegram</c>, ...) extends — is the concrete sealed
/// <see cref="ThalosBuilder"/> class in <c>Thalos.NET</c>. This follows that existing, real convention instead.
/// </remarks>
public static class WorkflowOrmThalosBuilderExtensions
{
    /// <summary>
    /// Uses <see cref="OrmWorkflowStore"/> as the <see cref="IWorkflowStore"/> and
    /// <see cref="OrmProcessDefinitionStore"/> as the <see cref="IProcessDefinitionStore"/>, replacing any
    /// earlier registration of either. Does not register <see cref="ProcessDefinitionSync"/> or an
    /// <c>IProcessDefinitionSource</c> — syncing is an engine-level concern and where definitions come from is
    /// host policy, so a host composes those itself from the <see cref="IProcessDefinitionStore"/> registered
    /// here. When <see cref="WorkflowOrmOptions.EnsureSchemaOnStartup"/> is set (the default), also registers a
    /// hosted service that applies the outbox and workflow migrations at startup.
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
        // One IProcessDefinitionStore singleton, wrapped in the cache, resolved by both consumers: the dispatcher
        // that runs a node and the workflow store that resumes a gate share one cache and one answer for what a
        // process is. Registering the cache here rather than letting each consumer hold its own is what keeps that
        // "one answer" true — two caches would be two things to invalidate and two chances to disagree.
        services.Replace(ServiceDescriptor.Singleton<IProcessDefinitionStore>(sp =>
            new CachingProcessDefinitionStore(new OrmProcessDefinitionStore(sp.GetRequiredService<WorkflowOrmOptions>()))));
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStore>(sp =>
            new OrmWorkflowStore(sp.GetRequiredService<WorkflowOrmOptions>(), sp.GetRequiredService<IProcessDefinitionStore>())));

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
