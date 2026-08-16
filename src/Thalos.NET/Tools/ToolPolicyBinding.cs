namespace Thalos.Tools;

/// <summary>Requires policy <paramref name="PolicyName"/> for tools whose qualified name matches <paramref name="ToolPattern"/>.</summary>
/// <param name="ToolPattern">A <see cref="Glob"/> pattern matched against the qualified tool name (<c>{source}__{tool}</c>).</param>
/// <param name="PolicyName">The <c>[Policy]</c> name of the ZeroAlloc.Authorization policy that must pass.</param>
public sealed record ToolPolicyBinding(string ToolPattern, string PolicyName)
{
    /// <summary>Returns true when <paramref name="qualifiedToolName"/> matches <see cref="ToolPattern"/>.</summary>
    public bool Matches(string qualifiedToolName) => Glob.IsMatch(ToolPattern, qualifiedToolName);
}
