namespace Thalos.Tests.Unit.Workspaces;

/// <summary>
/// Shared by every test in this folder that needs a real symlink or junction: reports a test as Skipped — never
/// Passed — when the current machine cannot create one, so a privilege gap on a developer's Windows box does not
/// mask a real regression, while still failing loudly under CI, where the platform is expected to support it.
/// </summary>
internal static class LinkTestHelpers
{
    /// <summary>
    /// Reports the calling test as Skipped, or throws under <c>CI</c> — see the type-level remarks.
    /// </summary>
    /// <param name="action">What could not be done, e.g. <c>"create a directory link (junction or symlink)"</c>.</param>
    /// <param name="ex">The exception the attempt threw, if any; included in the skip reason.</param>
    public static void FailOrSkip(string action, Exception? ex)
    {
        if (Environment.GetEnvironmentVariable("CI") is not null)
        {
            throw new InvalidOperationException($"Could not {action} under CI, where this platform is expected to support it.", ex);
        }

        Skip.If(true, $"Could not {action} on this machine{(ex is null ? "" : $": {ex.Message}")}.");
    }
}
