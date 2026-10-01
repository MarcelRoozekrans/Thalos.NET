using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>Everything needed to create one sandbox.</summary>
public sealed record SandboxSpec
{
    /// <summary>The workflow run the sandbox serves.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The container image.</summary>
    public required string Image { get; init; }

    /// <summary>Per-sandbox bearer token, 32 random bytes as base64url. The host requires it on every request.</summary>
    public required string Token { get; init; }

    /// <summary>null = any extension, which is "*" on the wire. Entries must not be empty, contain a semicolon or be "*"; <see cref="Validate"/> enforces that.</summary>
    public required IReadOnlySet<string>? AllowedWriteExtensions { get; init; }

    /// <summary>Paths a change may not touch. Entries must not contain a semicolon; <see cref="Validate"/> enforces that.</summary>
    public required ProtectedPathSet ProtectedPaths { get; init; }

    /// <summary>Resource limits.</summary>
    public required SandboxLimits Limits { get; init; }

    /// <summary>The run id in "N" format.</summary>
    public string SandboxId => RunId.ToString("N");

    /// <summary>Checks that nothing in the spec can corrupt the semicolon-joined environment values.</summary>
    /// <returns>Success, or a validation error naming the offending entry.</returns>
    public UnitResult<AgentError> Validate()
    {
        if (AllowedWriteExtensions is not null)
        {
            foreach (var ext in AllowedWriteExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext))
                {
                    return UnitResult<AgentError>.Failure(AgentError.Validation("An allowed write extension must not be empty or whitespace."));
                }

                if (ext.Contains(';', StringComparison.Ordinal))
                {
                    return UnitResult<AgentError>.Failure(AgentError.Validation($"Allowed write extension '{ext}' must not contain a semicolon."));
                }

                if (string.Equals(ext, "*", StringComparison.Ordinal))
                {
                    return UnitResult<AgentError>.Failure(AgentError.Validation("The allowed write extension '*' is reserved for any; use a null set instead."));
                }
            }
        }

        foreach (var entry in ProtectedPaths.Entries)
        {
            if (entry.Contains(';', StringComparison.Ordinal))
            {
                return UnitResult<AgentError>.Failure(AgentError.Validation($"Protected path '{entry}' must not contain a semicolon."));
            }
        }

        return UnitResult<AgentError>.Success();
    }

    /// <summary>The container's complete environment (S1): the runtime passes exactly this and nothing else.</summary>
    /// <param name="egressProxy">The only route out of the sandbox.</param>
    /// <returns>The environment variables.</returns>
    public IReadOnlyDictionary<string, string> Environment(Uri egressProxy)
    {
        ArgumentNullException.ThrowIfNull(egressProxy);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SandboxEnvironment.RunId] = RunId.ToString("D"),
            [SandboxEnvironment.Token] = Token,
            [SandboxEnvironment.WriteExtensions] = AllowedWriteExtensions is null ? "*" : string.Join(';', AllowedWriteExtensions.Order(StringComparer.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase)),
            [SandboxEnvironment.ProtectedPaths] = string.Join(';', ProtectedPaths.Entries),
            ["HTTPS_PROXY"] = egressProxy.ToString(),
            ["HTTP_PROXY"] = egressProxy.ToString(),
            ["NO_PROXY"] = "localhost,127.0.0.1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["NUGET_PACKAGES"] = "/work/nuget",
            ["HOME"] = "/work/home",
        };
    }
}
