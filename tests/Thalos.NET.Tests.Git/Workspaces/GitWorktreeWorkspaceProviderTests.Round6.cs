namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Follow-up to task A6's controller adjudication: a first clone killed mid-way left a <c>mirrors/.tmp-*</c>
/// directory that nothing ever removed. <see cref="Thalos.Git.Workspaces.GitWorktreeWorkspaceProvider"/> now sweeps
/// stale temporary clone directories for the repository it is about to clone, under that repository's mirror lock
/// (see <c>CloneMirrorAsync</c>, which calls the sweep before computing its own temp path), and only for that
/// repository — a shared <c>mirrors</c> directory can hold another repository's own, still-live, temp directory at
/// the same moment, under its own lock.
/// </summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    /// <summary>
    /// A stale <c>.tmp-*</c> directory left by an earlier, killed clone of the same repository ("sandbox", the
    /// repository <see cref="Request"/> always uses) must be gone once a later create for that repository
    /// finishes. Before this fix, nothing ever swept it; red recorded by removing the
    /// <c>SweepStaleCloneTempDirectories</c> call in <c>CloneMirrorAsync</c>.
    /// </summary>
    [Fact]
    public async Task Create_sweeps_a_stale_temporary_clone_directory_for_the_same_repository()
    {
        using var remote = LocalGitRemote.Create();
        var mirrorsDir = Path.Combine(_dataRoot, "mirrors");
        Directory.CreateDirectory(mirrorsDir);
        var stale = Path.Combine(mirrorsDir, $".tmp-{Guid.NewGuid():N}-sandbox");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "marker"), "left behind by a killed clone");
        var runId = Guid.NewGuid();

        var result = await Provider(out _).CreateAsync(Request(remote, runId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Directory.Exists(stale).Should().BeFalse("a stale temporary clone directory for the same repository must be swept before the next clone");
    }

    /// <summary>
    /// A stale <c>.tmp-*</c> directory belonging to a <em>different</em> repository, sharing the same
    /// <c>mirrors</c> directory, must never be touched by a create for another repository: the two never share a
    /// mirror lock, so one repository's create could be sweeping while the other repository's own clone is still
    /// mid-flight into what looks, by prefix alone, like the same kind of leftover. Red recorded by matching on the
    /// <c>.tmp-</c> prefix alone, without comparing the repository name the directory carries.
    /// </summary>
    [Fact]
    public async Task Create_never_sweeps_a_stale_temporary_clone_directory_for_a_different_repository()
    {
        using var remote = LocalGitRemote.Create();
        var mirrorsDir = Path.Combine(_dataRoot, "mirrors");
        Directory.CreateDirectory(mirrorsDir);
        var staleForOther = Path.Combine(mirrorsDir, $".tmp-{Guid.NewGuid():N}-some-other-repo");
        Directory.CreateDirectory(staleForOther);
        var runId = Guid.NewGuid();

        var result = await Provider(out _).CreateAsync(Request(remote, runId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Directory.Exists(staleForOther).Should().BeTrue("a stale temporary clone directory for a different repository must never be swept");
    }
}
