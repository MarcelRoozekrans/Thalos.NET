using System.Diagnostics;
using System.Text;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Writes the wrapper, spy and hook scripts these tests hand to git or run as git, so that they can be executed at
/// once even while other tests in the same process are starting processes.
/// </summary>
/// <remarks>
/// On Linux, <c>execve</c> refuses a file that any process holds open for writing, with ETXTBSY, "Text file busy". A
/// script written with <see cref="File.WriteAllText(string, string?)"/> is open for writing in this process for a
/// moment, and a child that another test thread forks in that moment inherits a copy of that descriptor and keeps it
/// until the child itself calls <c>exec</c>. Close-on-exec does not help, since the copy lives between the fork and
/// the exec. A test that runs its script straight after writing it then fails to start it, now and then, and only
/// under parallel load. So on Linux and macOS the script is written by a <c>/bin/sh</c> child, whose <c>cat</c> holds
/// the only writable descriptor and has exited before this returns: this multi-threaded process never holds one, so
/// no fork can inherit one. Windows has no such rule, and a <c>.cmd</c> script is read, not executed as an image.
/// </remarks>
internal static class ExecutableScript
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> and, outside Windows, makes it executable, mode 755.</summary>
    /// <exception cref="InvalidOperationException">The script could not be written.</exception>
    public static void Write(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), // a BOM before #! breaks the shebang
        };
        foreach (var arg in new[] { "-c", "cat > \"$1\" && chmod 755 \"$1\"", "sh", path })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start /bin/sh.");
        process.StandardInput.Write(content);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not write the executable script '{path}': {error}");
        }
    }
}
