using Thalos.Tools;

namespace Thalos;

/// <summary>Bindable options (section "Thalos").</summary>
public sealed class ThalosOptions
{
    /// <summary>
    /// Configuration section name. Binding this class from <c>IConfiguration</c> is a follow-up: <see cref="AgentDefinition.Id"/>
    /// is a typed id and needs a type converter before the configuration binder can materialise it. Use <see cref="ThalosBuilder.AddAgent"/> for now.
    /// </summary>
    public const string SectionName = "Thalos";

    /// <summary>Agent definitions served by <see cref="Agents.OptionsAgentCatalog"/>.</summary>
    public IList<AgentDefinition> Agents { get; } = [];

    /// <summary>Tool-pattern → authorization-policy bindings evaluated by <see cref="DefaultToolAuthorizer"/>.</summary>
    public IList<ToolPolicyBinding> ToolPolicies { get; } = [];
}
