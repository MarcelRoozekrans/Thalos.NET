namespace Thalos.Tools;

/// <summary>Requires policy <paramref name="PolicyName"/> for tools whose qualified name matches <paramref name="ToolPattern"/>.</summary>
public sealed record ToolPolicyBinding(string ToolPattern, string PolicyName)
{
    public bool Matches(string qualifiedToolName) => Glob.IsMatch(ToolPattern, qualifiedToolName);
}
