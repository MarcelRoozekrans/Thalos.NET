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

    /// <summary>How many bytes of the end of the output a tool result carries.</summary>
    public int OutputTailBytes { get; set; } = 16 * 1024;
}
