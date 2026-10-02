using Microsoft.Extensions.Configuration;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Host;

/// <summary>
/// The sandbox host's settings, read once at startup from configuration: the container's environment, where the
/// runtime puts the <see cref="SandboxEnvironment"/> keys, and the command line, where an in-process host passes them.
/// </summary>
/// <param name="RunId">The run the sandbox serves; the only run any call is confined to.</param>
/// <param name="Token">The per-sandbox bearer token every request must carry.</param>
/// <param name="AllowAnyWriteExtension">True when the write-extension setting is <c>*</c>.</param>
/// <param name="WriteExtensions">The writable extensions when <paramref name="AllowAnyWriteExtension"/> is false; empty otherwise.</param>
/// <param name="ProtectedPaths">The protected path entries, as <see cref="ProtectedPathSet"/> takes them.</param>
/// <param name="RoslynCommand">The RoslynCodeLens executable.</param>
/// <param name="RoslynArgs">Arguments placed before the solution path.</param>
/// <param name="RoslynReload">The run-scoped reload setting, <c>tool:rebuild_solution</c> unless configured.</param>
/// <param name="WorkRoot">The work volume, <c>/work</c> unless configured.</param>
public sealed record SandboxSettings(
    Guid RunId,
    string Token,
    bool AllowAnyWriteExtension,
    IReadOnlySet<string> WriteExtensions,
    IReadOnlyList<string> ProtectedPaths,
    string RoslynCommand,
    IReadOnlyList<string> RoslynArgs,
    string RoslynReload,
    string WorkRoot)
{
    /// <summary>The RoslynCodeLens executable. Default <c>roslyn-codelens-mcp</c>.</summary>
    public const string RoslynCommandKey = "THALOS_SANDBOX_ROSLYN_COMMAND";

    /// <summary>Optional, <c>;</c>-separated arguments placed before the solution path.</summary>
    public const string RoslynArgsKey = "THALOS_SANDBOX_ROSLYN_ARGS";

    /// <summary>The Roslyn server's reload setting. Default <c>tool:rebuild_solution</c>; tests set <c>none</c>.</summary>
    public const string RoslynReloadKey = "THALOS_SANDBOX_ROSLYN_RELOAD";

    /// <summary>The work volume. Default <c>/work</c>; tests point it at a temporary directory.</summary>
    public const string WorkRootKey = "THALOS_SANDBOX_WORK_ROOT";

    /// <summary>The shortest token accepted. The runtime issues 32 random bytes as base64url, 43 characters.</summary>
    public const int MinTokenLength = 32;

    /// <summary>Where the run's repository is checked out, under <see cref="WorkRoot"/>.</summary>
    public string RepoRoot => Path.Combine(WorkRoot, "repo");

    /// <summary>
    /// Reads and validates the settings. Fails, naming the key, when the run id is not a GUID, the token is missing or
    /// shorter than <see cref="MinTokenLength"/>, the write-extension or protected-path setting is absent, or the work
    /// root is not absolute. The host refuses to start on a failure.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    public static Result<SandboxSettings, AgentError> Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!Guid.TryParse(configuration[SandboxEnvironment.RunId], out var runId) || runId == Guid.Empty)
        {
            return Invalid($"{SandboxEnvironment.RunId} must be a non-empty GUID.");
        }

        var token = configuration[SandboxEnvironment.Token];
        if (string.IsNullOrWhiteSpace(token) || token.Length < MinTokenLength)
        {
            return Invalid($"{SandboxEnvironment.Token} must be set, at least {MinTokenLength} characters.");
        }

        // Absent is refused; present and empty is a grant of no extension, which refuses every write.
        if (configuration[SandboxEnvironment.WriteExtensions] is not { } extensions)
        {
            return Invalid($"{SandboxEnvironment.WriteExtensions} must be set: '*' or a ';'-separated extension list.");
        }

        if (configuration[SandboxEnvironment.ProtectedPaths] is not { } protectedPaths)
        {
            return Invalid($"{SandboxEnvironment.ProtectedPaths} must be set.");
        }

        var workRoot = configuration[WorkRootKey] is { Length: > 0 } configuredRoot ? configuredRoot : "/work";
        if (!Path.IsPathFullyQualified(workRoot))
        {
            return Invalid($"{WorkRootKey} must be an absolute path.");
        }

        var any = string.Equals(extensions.Trim(), "*", StringComparison.Ordinal);
        return Result<SandboxSettings, AgentError>.Success(new SandboxSettings(
            runId,
            token,
            any,
            any ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : Split(extensions).ToHashSet(StringComparer.OrdinalIgnoreCase),
            Split(protectedPaths),
            configuration[RoslynCommandKey] is { Length: > 0 } command ? command : "roslyn-codelens-mcp",
            Split(configuration[RoslynArgsKey] ?? ""),
            configuration[RoslynReloadKey] is { Length: > 0 } reload ? reload : "tool:rebuild_solution",
            Path.GetFullPath(workRoot)));
    }

    private static string[] Split(string value) => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static Result<SandboxSettings, AgentError> Invalid(string message) =>
        Result<SandboxSettings, AgentError>.Failure(AgentError.Validation(message));

    /// <summary>The token is never printed, so the record's generated text leaves it out.</summary>
    public override string ToString() => $"SandboxSettings {{ RunId = {RunId}, WorkRoot = {WorkRoot} }}";
}
