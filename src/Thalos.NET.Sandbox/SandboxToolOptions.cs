namespace Thalos.Sandbox;

/// <summary>Limits for the in-sandbox <c>build</c> and <c>test</c> tools.</summary>
public sealed class SandboxToolOptions
{
    /// <summary>The tool source name the tools register under.</summary>
    public const string SourceName = "sandbox";

    /// <summary>How long <c>dotnet build</c> may run.</summary>
    public TimeSpan BuildTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long <c>dotnet test</c> may run.</summary>
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Where <c>sandbox__build</c> and <c>sandbox__test</c> make a throwaway copy of the worktree to run in, so nothing a
    /// build or test writes reaches the worktree the run exports. The copy leaves out <c>.git</c>, <c>bin</c> and
    /// <c>obj</c> at any depth, restores inside itself, and is deleted afterwards. Null runs in the worktree itself.
    /// </summary>
    public string? ScratchRoot { get; set; }

    /// <summary>How many bytes of the end of the output a tool result carries.</summary>
    public int OutputTailBytes { get; set; } = 16 * 1024;
}
