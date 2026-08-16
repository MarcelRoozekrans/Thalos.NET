using System.Reflection;
using System.Text.Json;
using ZeroAlloc.Authorization;

namespace Thalos.Tools;

/// <summary>
/// Evaluates every <see cref="ToolPolicyBinding"/> whose pattern matches the tool; all must pass.
/// Policies are looked up by their <see cref="PolicyAttribute"/> name (same convention as AI.Sentinel).
/// A bound-but-unregistered policy denies (fail closed). No matching binding → allow.
/// </summary>
public sealed class DefaultToolAuthorizer : IToolAuthorizer
{
    private readonly IReadOnlyList<ToolPolicyBinding> _bindings;
    private readonly Dictionary<string, IAuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    public DefaultToolAuthorizer(IEnumerable<ToolPolicyBinding> bindings, IEnumerable<IAuthorizationPolicy> policies)
    {
        _bindings = bindings.ToList();
        foreach (var policy in policies)
        {
            // Reflection is intentional here: ZeroAlloc.Authorization identifies policies by [Policy("name")]
            // and there is no non-reflective way to read it. Runs once at construction, never per call.
            if (policy.GetType().GetCustomAttribute<PolicyAttribute>(inherit: false) is { } attr)
            {
                _policies[attr.Name] = policy;
            }
        }
    }

    public async ValueTask<ToolAuthorizationDecision> AuthorizeAsync(ISecurityContext caller, string qualifiedToolName, JsonElement arguments, CancellationToken ct)
    {
        foreach (var binding in _bindings)
        {
            if (!binding.Matches(qualifiedToolName))
            {
                continue;
            }

            if (!_policies.TryGetValue(binding.PolicyName, out var policy))
            {
                return ToolAuthorizationDecision.Deny($"Policy '{binding.PolicyName}' required for '{qualifiedToolName}' is not registered.");
            }

            var result = await policy.EvaluateAsync(caller, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return ToolAuthorizationDecision.Deny($"{result.Error.Code}: {result.Error.Reason}");
            }
        }

        return ToolAuthorizationDecision.Allow();
    }
}
