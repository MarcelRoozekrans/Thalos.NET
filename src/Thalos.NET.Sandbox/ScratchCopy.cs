namespace Thalos.Sandbox;

/// <summary>
/// A throwaway copy of a run's worktree that <c>sandbox__build</c> and <c>sandbox__test</c> run in, so what a build or a
/// test writes into its working directory never reaches the worktree the run exports. Holds every file of the worktree,
/// tracked or not, except <c>.git</c>, <c>bin</c> and <c>obj</c> directories at any depth. Links are not copied, so the
/// copy never reaches outside the worktree. Disposing deletes it.
/// </summary>
internal sealed class ScratchCopy : IDisposable
{
    /// <summary>The directory names left out at any depth, compared case-insensitively.</summary>
    private static readonly string[] Excluded = [".git", "bin", "obj"];

    /// <summary>How many times disposal tries to delete the copy, a short pause apart, before leaving it behind.</summary>
    private const int DeleteAttempts = 5;

    private ScratchCopy(string root) => Root = root;

    /// <summary>The copy's root directory.</summary>
    public string Root { get; }

    /// <summary>Copies <paramref name="source"/> into a new directory under <paramref name="scratchRoot"/>.</summary>
    /// <param name="source">The worktree.</param>
    /// <param name="scratchRoot">Where copies are made.</param>
    public static ScratchCopy Create(string source, string scratchRoot)
    {
        var copy = new ScratchCopy(Path.Combine(scratchRoot, Guid.NewGuid().ToString("N")));
        try
        {
            CopyDirectory(new DirectoryInfo(source), Directory.CreateDirectory(copy.Root));
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
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

    private static void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                if (!Array.Exists(Excluded, e => string.Equals(e, directory.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    CopyDirectory(directory, target.CreateSubdirectory(directory.Name));
                }
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(Path.Combine(target.FullName, file.Name));
            }
        }
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
