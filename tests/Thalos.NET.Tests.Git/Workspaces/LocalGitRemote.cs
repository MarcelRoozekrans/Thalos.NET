using System.Diagnostics;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// A real bare git repository in a temp directory, seeded with one commit on <c>main</c>, for
/// <see cref="Thalos.Git.Workspaces.GitWorktreeWorkspaceProvider"/> tests to clone and fetch from as a genuine git
/// remote — no fake transport, no mocked git. Every commit is made with <c>-c user.name=t -c user.email=t@example.invalid</c>,
/// because CI has no configured git identity.
/// </summary>
public sealed class LocalGitRemote : IDisposable
{
    private readonly string _path;

    private LocalGitRemote(string path) => _path = path;

    /// <summary>The bare repository's path — usable directly as a git remote URL, since a local path is a valid one.</summary>
    public string Url => _path;

    /// <summary>
    /// Creates a bare repository seeded with <c>README.md</c> and <c>AGENT.md</c> on <c>main</c>. Each entry in
    /// <paramref name="files"/> overrides the default content of a name already seeded, or adds a new file.
    /// </summary>
    /// <remarks>
    /// A second branch, <c>other</c>, is always seeded with one commit newer than <c>main</c>'s tip, and the bare
    /// repository's own <c>HEAD</c> is pointed at <c>other</c>, not <c>main</c> — so a provider that cuts a
    /// worktree from the mirror's <c>HEAD</c> instead of the requested <c>origin/&lt;DefaultBranch&gt;</c> checks
    /// out different, wrong content, and a test asserting against <c>main</c>'s own tip catches it directly,
    /// without any extra setup at the call site.
    /// </remarks>
    public static LocalGitRemote Create(params (string Name, string Content)[] files)
    {
        var seeds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "# sandbox\n",
            ["AGENT.md"] = "# sandbox\n",
        };

        foreach (var (name, content) in files)
        {
            seeds[name] = content;
        }

        var bare = Directory.CreateTempSubdirectory("thalos-git-remote-bare-").FullName;
        RunGit(bare, "init", "--bare", "--initial-branch=main");

        var seed = Directory.CreateTempSubdirectory("thalos-git-remote-seed-").FullName;
        try
        {
            RunGit(seed, "init", "--initial-branch=main");
            foreach (var (name, content) in seeds)
            {
                File.WriteAllText(Path.Combine(seed, name), content);
            }

            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "add", "-A");
            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "seed");
            RunGit(seed, "remote", "add", "origin", bare);
            RunGit(seed, "push", "origin", "main");

            RunGit(seed, "checkout", "-b", "other");
            File.WriteAllText(Path.Combine(seed, "OTHER-ONLY.md"), "# other\n");
            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "add", "-A");
            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "other");
            RunGit(seed, "push", "origin", "other");
            RunGit(bare, "symbolic-ref", "HEAD", "refs/heads/other");
        }
        finally
        {
            DeleteReadOnly(seed);
        }

        return new LocalGitRemote(bare);
    }

    /// <summary>The commit sha at the tip of <paramref name="branch"/> in the bare repository.</summary>
    public string HeadOf(string branch) => RunGit(_path, "rev-parse", branch);

    /// <summary>
    /// Creates a bare repository seeded with <c>README.md</c> plus one entry, <paramref name="linkName"/>, committed
    /// as a real git symlink (mode <c>120000</c>) pointing at <paramref name="linkTarget"/>. The symlink is written
    /// as a git object directly — <c>hash-object</c> plus <c>update-index --cacheinfo</c> — rather than created on
    /// the filesystem, so this works on every OS this test suite runs on, including Windows, where creating an
    /// actual filesystem symlink needs a privilege this process does not have. What matters for the test that uses
    /// this is only that the *repository* records a symlink; what a checkout does with it is exactly the question
    /// under test.
    /// </summary>
    public static LocalGitRemote CreateWithSymlink(string linkName, string linkTarget)
    {
        var bare = Directory.CreateTempSubdirectory("thalos-git-remote-bare-").FullName;
        RunGit(bare, "init", "--bare", "--initial-branch=main");

        var seed = Directory.CreateTempSubdirectory("thalos-git-remote-seed-").FullName;
        try
        {
            RunGit(seed, "init", "--initial-branch=main");
            File.WriteAllText(Path.Combine(seed, "README.md"), "# sandbox\n");
            RunGit(seed, "add", "README.md");

            var blobSha = RunGitWithStdin(seed, linkTarget, "hash-object", "-w", "--stdin");
            RunGit(seed, "update-index", "--add", "--cacheinfo", $"120000,{blobSha},{linkName}");

            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "seed");
            RunGit(seed, "remote", "add", "origin", bare);
            RunGit(seed, "push", "origin", "main");
        }
        finally
        {
            DeleteReadOnly(seed);
        }

        return new LocalGitRemote(bare);
    }

    /// <inheritdoc />
    public void Dispose() => DeleteReadOnly(_path);

    /// <summary>
    /// Deletes a directory git created, clearing the read-only attribute git sets on files under <c>.git/objects</c>
    /// first — otherwise a plain recursive delete throws <see cref="UnauthorizedAccessException"/> on Windows.
    /// </summary>
    private static void DeleteReadOnly(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Runs a git command synchronously and returns its trimmed standard output, for test setup and assertions.</summary>
    internal static string RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stdErr}");
        }

        return stdOut.Trim();
    }

    /// <summary>Same as <see cref="RunGit"/>, but writes <paramref name="input"/> to the process's standard input first — for <c>git hash-object --stdin</c>.</summary>
    private static string RunGitWithStdin(string workingDirectory, string input, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stdErr}");
        }

        return stdOut.Trim();
    }
}
