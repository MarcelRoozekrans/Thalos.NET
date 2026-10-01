using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;

namespace Thalos.Sandbox;

/// <summary>
/// The <c>sandbox</c> tool source's methods: <c>sandbox__build</c> and <c>sandbox__test</c>. They run inside a run's
/// sandbox container against the run's workspace. The command lines are fixed argument lists passed through
/// <see cref="ProcessSpec.Arguments"/>, never a shell string, and the only agent-controlled value, the test filter,
/// is validated against a strict allow-list and refused when it could be read as an option.
/// </summary>
/// <param name="workspaces">Looks up the calling run's workspace.</param>
/// <param name="runner">Runs the <c>dotnet</c> process.</param>
/// <param name="options">Timeouts and output size.</param>
/// <param name="logger">Logs refusals.</param>
[ThalosToolType]
public sealed partial class SandboxTools(IRunWorkspaceProvider workspaces, ISandboxProcessRunner runner, SandboxToolOptions options, ILogger<SandboxTools> logger)
{
    private const string NoWorkspace = "error: this turn has no run workspace";
    private const string FilterRefused = "error: filter refused";
    private const string DotNet = "dotnet";

    /// <summary><c>sandbox__build</c>: runs <c>dotnet build</c> on the run's solution.</summary>
    [ThalosTool("build")]
    [Description("Build the run's solution with `dotnet build`. Returns the exit code, a summary and the tail of the output.")]
    public async Task<string> Build(ISecurityContext caller, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(caller, ct).ConfigureAwait(false);
        if (workspace is null)
        {
            return NoWorkspace;
        }

        List<string> arguments = ["build", TargetOf(workspace), "--nologo", "-v:q", "-clp:ErrorsOnly"];
        var outcome = await runner.RunAsync(new ProcessSpec(DotNet, arguments, workspace.Root, options.BuildTimeout), ct).ConfigureAwait(false);
        return Format(outcome, options.BuildTimeout, BuildSummary(outcome.FullOutput));
    }

    /// <summary><c>sandbox__test</c>: runs <c>dotnet test</c> on the run's solution, optionally filtered.</summary>
    [ThalosTool("test")]
    [Description("Run the run's tests with `dotnet test`, optionally filtered. Returns the exit code, a pass/fail summary and the tail of the output.")]
    public async Task<string> Test(ISecurityContext caller, [Description("Optional `dotnet test --filter` expression, e.g. `FullyQualifiedName~Orders`.")] string? filter = null, CancellationToken ct = default)
    {
        if (filter is not null && !IsValidFilter(filter))
        {
            LogFilterRefused(logger, filter.Length);
            return FilterRefused;
        }

        var workspace = await FindWorkspaceAsync(caller, ct).ConfigureAwait(false);
        if (workspace is null)
        {
            return NoWorkspace;
        }

        List<string> arguments = ["test", TargetOf(workspace), "--nologo", "-v:q"];
        if (filter is not null)
        {
            arguments.Add("--filter");
            arguments.Add(filter);
        }

        var outcome = await runner.RunAsync(new ProcessSpec(DotNet, arguments, workspace.Root, options.TestTimeout), ct).ConfigureAwait(false);
        return Format(outcome, options.TestTimeout, TestSummary(outcome.FullOutput));
    }

    /// <summary>
    /// The allow-list for a filter: 1 to 256 characters of the listed set, and no leading <c>-</c>. The pattern ends
    /// in <c>\z</c>, not <c>$</c>, because <c>$</c> also matches before a trailing newline.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.~=!&|()\-,: ]{1,256}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilterPattern();

    internal static bool IsValidFilter(string filter) => !filter.StartsWith('-') && FilterPattern().IsMatch(filter);

    private async Task<RunWorkspace?> FindWorkspaceAsync(ISecurityContext caller, CancellationToken ct) =>
        RunWorkspaceClaims.RunIdOf(caller) is { } runId
            ? await workspaces.FindAsync(runId, ct).ConfigureAwait(false)
            : null;

    private static string TargetOf(RunWorkspace workspace) => workspace.SolutionPath ?? workspace.Root;

    private string Format(ProcessOutcome outcome, TimeSpan timeout, string summary)
    {
        var exit = outcome.TimedOut ? $"timed out after {timeout:c}" : outcome.ExitCode!.Value.ToString(CultureInfo.InvariantCulture);
        var tail = Tail(outcome.FullOutput, options.OutputTailBytes);
        return $"exit: {exit}\n{summary}\n--- output (last {Encoding.UTF8.GetByteCount(tail)} bytes) ---\n{tail}";
    }

    /// <summary>The last <paramref name="maxBytes"/> bytes of <paramref name="text"/> as UTF-8, without starting mid-character.</summary>
    private static string Tail(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
        {
            start++;
        }

        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static string BuildSummary(string output)
    {
        var errors = Lines(output).Count(l => l.Contains(": error ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error ", StringComparison.OrdinalIgnoreCase));
        return $"errors: {errors}";
    }

    /// <summary>The <c>Passed!</c> or <c>Failed!</c> line, or the <c>Total tests:</c> block, from the full output.</summary>
    private static string TestSummary(string output)
    {
        var lines = Lines(output).ToList();
        var result = lines.FindLast(l => l.StartsWith("Passed!", StringComparison.Ordinal) || l.StartsWith("Failed!", StringComparison.Ordinal));
        if (result is not null)
        {
            return result;
        }

        var total = lines.FindLastIndex(l => l.StartsWith("Total tests:", StringComparison.Ordinal));
        if (total < 0)
        {
            return "(no test summary found)";
        }

        var block = lines.Skip(total).Take(4).Select(l => l.Trim());
        return string.Join("; ", block);
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r').TrimStart());

    [LoggerMessage(EventId = 5901, Level = LogLevel.Warning, Message = "A sandbox test filter of {Length} characters was refused.")]
    private static partial void LogFilterRefused(ILogger logger, int length);
}
