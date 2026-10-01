namespace Thalos.Sandbox;

/// <summary>Names of the environment variables a sandbox container receives from the host.</summary>
public static class SandboxEnvironment
{
    /// <summary>The run id.</summary>
    public const string RunId = "THALOS_SANDBOX_RUN_ID";

    /// <summary>The per-sandbox bearer token.</summary>
    public const string Token = "THALOS_SANDBOX_TOKEN";

    /// <summary>Semicolon-joined writable extensions, or "*" for any.</summary>
    public const string WriteExtensions = "THALOS_SANDBOX_WRITE_EXTENSIONS";

    /// <summary>Semicolon-joined protected path entries.</summary>
    public const string ProtectedPaths = "THALOS_SANDBOX_PROTECTED_PATHS";
}
