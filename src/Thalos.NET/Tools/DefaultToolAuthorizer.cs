using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Authorization;

namespace Thalos.Tools;

/// <summary>
/// Evaluates every <see cref="ToolPolicyBinding"/> whose pattern matches the tool; all must pass.
/// Policies are looked up by their <see cref="PolicyAttribute"/> name (same convention as AI.Sentinel).
/// A bound-but-unregistered policy denies (fail closed) with a generic reason — the detailed reason is logged.
/// No matching binding → allow.
/// </summary>
/// <remarks>
/// Policy instances are captured at construction and reused for every call, so register policies as singletons.
/// Two policies carrying the same <c>[Policy]</c> name are a configuration error and throw at construction;
/// policies without a <c>[Policy]</c> attribute are ignored (logged at Debug).
/// </remarks>
public sealed partial class DefaultToolAuthorizer : IToolAuthorizer
{
    private const string NotAvailableReason = "tool is not available to this caller";

    private readonly IReadOnlyList<ToolPolicyBinding> _bindings;
    private readonly Dictionary<string, IAuthorizationPolicy> _policies = new(StringComparer.Ordinal);
    private readonly ILogger<DefaultToolAuthorizer> _logger;

    /// <summary>Creates an authorizer over <paramref name="bindings"/> resolved against <paramref name="policies"/>.</summary>
    /// <exception cref="InvalidOperationException">Two policies declare the same <c>[Policy]</c> name.</exception>
    public DefaultToolAuthorizer(IEnumerable<ToolPolicyBinding> bindings, IEnumerable<IAuthorizationPolicy> policies, ILogger<DefaultToolAuthorizer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(policies);
        _logger = logger ?? NullLogger<DefaultToolAuthorizer>.Instance;
        _bindings = bindings.ToList();
        foreach (var policy in policies)
        {
            var type = policy.GetType();
            // Reflection is intentional here: ZeroAlloc.Authorization identifies policies by [Policy("name")]
            // and there is no non-reflective way to read it. Runs once at construction, never per call.
            if (type.GetCustomAttribute<PolicyAttribute>(inherit: false) is not { } attr)
            {
                LogPolicyWithoutName(_logger, type.FullName ?? type.Name);
                continue;
            }

            if (_policies.TryGetValue(attr.Name, out var existing))
            {
                throw new InvalidOperationException($"Duplicate policy name '{attr.Name}': {existing.GetType().FullName} and {type.FullName}");
            }

            _policies[attr.Name] = policy;
        }
    }

    /// <inheritdoc />
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
                LogPolicyNotRegistered(_logger, binding.PolicyName, qualifiedToolName);
                return ToolAuthorizationDecision.Deny(NotAvailableReason);
            }

            var result = await policy.EvaluateAsync(caller, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return ToolAuthorizationDecision.Deny($"{result.Error.Code}: {result.Error.Reason}");
            }
        }

        return ToolAuthorizationDecision.Allow();
    }

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "Policy '{Policy}' required for '{Tool}' is not registered; denying")]
    private static partial void LogPolicyNotRegistered(ILogger logger, string policy, string tool);

    [LoggerMessage(EventId = 114, Level = LogLevel.Debug, Message = "Policy type '{PolicyType}' has no [Policy] attribute and is ignored")]
    private static partial void LogPolicyWithoutName(ILogger logger, string policyType);
}
