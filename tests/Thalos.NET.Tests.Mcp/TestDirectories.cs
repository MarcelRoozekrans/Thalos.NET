using System.Diagnostics;

namespace Thalos.Tests.Mcp;

/// <summary>Removes a test's temporary directory once every server that used it has been stopped.</summary>
internal static class TestDirectories
{
    /// <summary>
    /// Deletes <paramref name="path"/>, retrying for up to five seconds while Windows reports it in use. A server process
    /// that has exited, and that the registry has already waited for, can keep its working directory busy for a moment
    /// while the OS closes its handles, so the first attempt can fail with a sharing violation. A directory still busy after
    /// five seconds is a real holder, and the <see cref="IOException"/> is thrown.
    /// </summary>
    public static async Task DeleteAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (OperatingSystem.IsWindows() && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(100);
            }
        }
    }
}
