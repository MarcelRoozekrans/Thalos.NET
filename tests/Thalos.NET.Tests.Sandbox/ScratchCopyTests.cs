using Thalos.Sandbox;

namespace Thalos.Tests.Sandbox;

/// <summary>Fix round 1 of A8 (R26): the throwaway copy build and test run in.</summary>
public sealed class ScratchCopyTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-scratch-").FullName;

    /// <summary>Red 1: in ScratchCopy.CopyDirectory, copy every directory, excluded names included. Red 2: make Dispose a no-op.</summary>
    [Fact]
    public void The_copy_holds_the_tree_without_git_bin_and_obj_and_is_deleted()
    {
        var source = Path.Combine(_temp, "repo");
        foreach (var file in (string[])["App.slnx", "src/A.cs", ".git/config", "bin/a.dll", "src/obj/x.json", "src/Bin/y.dll", "untracked.txt"])
        {
            var path = Path.Combine(source, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }

        string root;
        using (var copy = ScratchCopy.Create(source, Path.Combine(_temp, "scratch")))
        {
            root = copy.Root;
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Should().BeEquivalentTo(["App.slnx", "src/A.cs", "untracked.txt"]);
            copy.Map(source, Path.Combine(source, "App.slnx")).Should().Be(Path.Combine(root, "App.slnx"));
        }

        Directory.Exists(root).Should().BeFalse();
        File.Exists(Path.Combine(source, "bin", "a.dll")).Should().BeTrue("the source is left alone");
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
}
