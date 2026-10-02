using System.Diagnostics;
using Thalos.Sandbox;

namespace Thalos.Tests.Sandbox;

/// <summary>A8 fix rounds 1 and 2: the throwaway copy build and test run in.</summary>
public sealed class ScratchCopyTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-scratch-").FullName;

    private string Source => Path.Combine(_temp, "repo");

    private string Scratch => Path.Combine(_temp, "scratch");

    /// <summary>Red 1: in ScratchCopy.CopyDirectoryAsync, copy every directory, excluded names included. Red 2: make Dispose a no-op.</summary>
    [Fact]
    public async Task The_copy_holds_the_tree_without_git_bin_and_obj_and_is_deleted()
    {
        Seed("App.slnx", "src/A.cs", ".git/config", "bin/a.dll", "src/obj/x.json", "src/Bin/y.dll", "untracked.txt");

        string root;
        using (var copy = (await ScratchCopy.CreateAsync(Source, Scratch, long.MaxValue, CancellationToken.None)).Value)
        {
            root = copy.Root;
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Should().BeEquivalentTo(["App.slnx", "src/A.cs", "untracked.txt"]);
            copy.Map(Source, Path.Combine(Source, "App.slnx")).Should().Be(Path.Combine(root, "App.slnx"));
        }

        Directory.Exists(root).Should().BeFalse();
        File.Exists(Path.Combine(Source, "bin", "a.dll")).Should().BeTrue("the source is left alone");
    }

    /// <summary>
    /// A FIFO planted in the worktree is left out, and the copy does not block on it. Red: in ScratchCopy.IsRegularFile,
    /// drop the statx check; opening the FIFO then blocks past the test's 30-second bound.
    /// </summary>
    [SkippableFact]
    public async Task A_fifo_in_the_worktree_is_skipped_without_blocking()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "a FIFO is a Linux file type here.");
        Seed("App.slnx");
        using (var mkfifo = Process.Start("mkfifo", [Path.Combine(Source, "pipe")]))
        {
            await mkfifo!.WaitForExitAsync();
            mkfifo.ExitCode.Should().Be(0);
        }

        var create = ScratchCopy.CreateAsync(Source, Scratch, long.MaxValue, CancellationToken.None);
        var finished = await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(30)));

        finished.Should().BeSameAs(create, "the copy must not block on the FIFO");
        using var copy = (await create).Value;
        File.Exists(Path.Combine(copy.Root, "App.slnx")).Should().BeTrue();
        Path.Exists(Path.Combine(copy.Root, "pipe")).Should().BeFalse();
    }

    /// <summary>Red: drop the ThrowIfCancellationRequested in ScratchCopy.CopyDirectoryAsync and pass no token to the reads; the copy then completes.</summary>
    [Fact]
    public async Task A_cancelled_copy_throws_and_leaves_nothing_behind()
    {
        Seed("App.slnx", "src/A.cs");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var create = async () => await ScratchCopy.CreateAsync(Source, Scratch, long.MaxValue, cancelled.Token);

        await create.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFileSystemEntries(Scratch).Should().BeEmpty();
    }

    /// <summary>Red: in ScratchCopy.CopyFileAsync, never charge the budget; the oversized copy then succeeds.</summary>
    [Fact]
    public async Task A_copy_over_the_byte_budget_fails_and_leaves_nothing_behind()
    {
        Seed("App.slnx", "src/A.cs");

        var created = await ScratchCopy.CreateAsync(Source, Scratch, maxBytes: 4, CancellationToken.None);

        created.IsFailure.Should().BeTrue();
        created.Error.Message.Should().Contain("more than 4 bytes");
        Directory.EnumerateFileSystemEntries(Scratch).Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private void Seed(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(Source, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
    }
}
