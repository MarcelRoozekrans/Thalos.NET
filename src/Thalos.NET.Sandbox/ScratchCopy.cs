using Thalos.Git.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>
/// A throwaway copy of a run's worktree that <c>sandbox__build</c> and <c>sandbox__test</c> run in, so what a build or a
/// test writes into its working directory never reaches the worktree the run exports. Holds every regular file of the
/// worktree, tracked or not, except under <c>.git</c>, <c>bin</c> and <c>obj</c> directories at any depth. Links, FIFOs,
/// sockets and devices are left out: a link could reach outside the worktree, and opening a FIFO blocks. Disposing
/// deletes the copy.
/// </summary>
/// <remarks>
/// The copy is bounded: by the caller's token, which the tool links to its own timeout; and by a byte budget, counted as
/// the bytes are written, so a file that grows during the copy cannot exceed it. On Linux the file type comes from
/// <c>statx</c>, because .NET reports a FIFO as an ordinary file; elsewhere the attributes decide. A file swapped for a
/// FIFO between the type check and the open would still block that one open; agent code would have to race the copy
/// to do it, and it gains nothing it could not do to its own run.
/// </remarks>
internal sealed class ScratchCopy : IDisposable
{
    /// <summary>The directory names left out at any depth, compared case-insensitively.</summary>
    private static readonly string[] Excluded = [".git", "bin", "obj"];

    /// <summary>How many times disposal tries to delete the copy, a short pause apart, before leaving it behind.</summary>
    private const int DeleteAttempts = 5;

    private const int BufferSize = 81920;

    private long _budget;

    private ScratchCopy(string root, long maxBytes)
    {
        Root = root;
        _budget = maxBytes;
    }

    /// <summary>The copy's root directory.</summary>
    public string Root { get; }

    /// <summary>
    /// Copies <paramref name="source"/> into a new directory under <paramref name="scratchRoot"/>. Fails, deleting what was
    /// copied, when the regular files hold more than <paramref name="maxBytes"/> or the file system refuses; a cancelled
    /// <paramref name="ct"/> deletes what was copied and throws.
    /// </summary>
    public static async Task<Result<ScratchCopy, AgentError>> CreateAsync(string source, string scratchRoot, long maxBytes, CancellationToken ct)
    {
        var copy = new ScratchCopy(Path.Combine(scratchRoot, Guid.NewGuid().ToString("N")), maxBytes);
        var done = false;
        try
        {
            var copied = await copy.CopyDirectoryAsync(new DirectoryInfo(source), Directory.CreateDirectory(copy.Root), ct).ConfigureAwait(false);
            done = copied;
            return copied
                ? Result<ScratchCopy, AgentError>.Success(copy)
                : Result<ScratchCopy, AgentError>.Failure(AgentError.Validation($"the workspace holds more than {maxBytes} bytes to copy"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<ScratchCopy, AgentError>.Failure(AgentError.ProviderError($"the workspace could not be copied: {ex.Message}"));
        }
        finally
        {
            if (!done)
            {
                copy.Dispose();
            }
        }
    }

    /// <summary><paramref name="path"/>, a path inside the worktree <paramref name="source"/>, mapped into the copy.</summary>
    public string Map(string source, string path) => Path.Combine(Root, Path.GetRelativePath(source, path));

    /// <summary>Deletes the copy, retrying a few times while a just-ended build still holds a file; a copy that still cannot be deleted is left on the work volume.</summary>
    public void Dispose()
    {
        for (var attempt = 1; attempt <= DeleteAttempts && Directory.Exists(Root); attempt++)
        {
            try
            {
                ClearReadOnly(new DirectoryInfo(Root));
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < DeleteAttempts)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }

    /// <summary>Whether <paramref name="file"/> is a regular file: not a link, FIFO, socket or device.</summary>
    internal static bool IsRegularFile(FileInfo file)
    {
        if (file.LinkTarget is not null
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Attributes.HasFlag(FileAttributes.Device)
            || file.Attributes.HasFlag(FileAttributes.Directory))
        {
            return false;
        }

        return !OperatingSystem.IsLinux() || UnixLinkCount.IsRegularFile(file.FullName) == true;
    }

    /// <summary>Copies one directory; false once the byte budget is spent.</summary>
    private async Task<bool> CopyDirectoryAsync(DirectoryInfo source, DirectoryInfo target, CancellationToken ct)
    {
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                if (!Array.Exists(Excluded, e => string.Equals(e, directory.Name, StringComparison.OrdinalIgnoreCase))
                    && !await CopyDirectoryAsync(directory, target.CreateSubdirectory(directory.Name), ct).ConfigureAwait(false))
                {
                    return false;
                }
            }
            else if (entry is FileInfo file && IsRegularFile(file)
                && !await CopyFileAsync(file, Path.Combine(target.FullName, file.Name), ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> CopyFileAsync(FileInfo file, string destination, CancellationToken ct)
    {
        var reader = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (reader.ConfigureAwait(false))
        {
            var writer = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await using (writer.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    _budget -= read;
                    if (_budget < 0)
                    {
                        return false;
                    }

                    await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
        }

        return true;
    }

    private static void ClearReadOnly(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}
