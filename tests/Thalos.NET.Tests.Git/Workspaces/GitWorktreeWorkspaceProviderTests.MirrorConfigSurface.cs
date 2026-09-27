namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Task A7, fix round 2, item 3: the provider owns the mirror's config. A reviewer's probes showed the mirror's own
/// config can redirect a push (Authorization header included) through <c>url.&lt;x&gt;.pushInsteadOf</c>, define
/// clean/smudge filter drivers that run on <c>add</c>, <c>reset</c> and <c>commit</c>, or widen a protocol
/// <see cref="Thalos.Git.Workspaces.GitCli"/> otherwise pins closed — none of which a command-line <c>-c</c> flag
/// can close, since the mirror's own tracked config is a separate, persistent layer. Mirror validation now reads
/// every config key git will use in the mirror, with its scope, and refuses the mirror outright when any key falls outside
/// <see cref="Thalos.Git.Workspaces.MirrorConfigSurface"/>'s allow-list — see that type's own remarks for why an
/// allow-list, not a deny-list. Each test here plants one disallowed key directly into an already-created mirror's
/// config (as a later run's own tampering, or a previous run's, would look), then makes a second create for the
/// same repository and expects it refused, closed, without touching the mirror.
/// </summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    [Fact]
    public async Task A_push_redirect_in_the_mirrors_config_makes_the_next_create_refuse()
    {
        using var remote = LocalGitRemote.Create();
        var first = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var mirror = MirrorOf("sandbox");
        // LocalGitRemote.RunGit, not the space-splitting Git() helper: remote.Url is a filesystem path that can
        // itself contain a space, which Git()'s naive Split(' ') would break apart into two arguments.
        LocalGitRemote.RunGit(mirror, "config", "url.https://evil.example/redirect.git.pushInsteadOf", remote.Url);

        var second = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        IsIntactBareRepository(mirror).Should().BeTrue("a mirror refused for disallowed config must never be deleted, the same as a positive-invalid mirror with live worktrees");
    }

    [Fact]
    public async Task A_filter_driver_in_the_mirrors_config_makes_the_next_create_refuse()
    {
        using var remote = LocalGitRemote.Create();
        var first = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var mirror = MirrorOf("sandbox");
        LocalGitRemote.RunGit(mirror, "config", "filter.x.clean", "cat");

        var second = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        IsIntactBareRepository(mirror).Should().BeTrue("a mirror refused for disallowed config must never be deleted");
    }

    [Fact]
    public async Task A_protocol_override_in_the_mirrors_config_makes_the_next_create_refuse()
    {
        using var remote = LocalGitRemote.Create();
        var first = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var mirror = MirrorOf("sandbox");
        LocalGitRemote.RunGit(mirror, "config", "protocol.http.allow", "always");

        var second = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        IsIntactBareRepository(mirror).Should().BeTrue("a mirror refused for disallowed config must never be deleted");
    }

    /// <summary>
    /// Fix round 4, item 1 (CRITICAL): the reviewer's exploit, a valueless <c>extensions.worktreeConfig</c> plus a
    /// filter in an existing worktree's <c>config.worktree</c> plus <c>*.cs filter=x</c> in the mirror's
    /// <c>info/attributes</c>, makes the next create for the same repository refuse, and leaves the mirror intact.
    /// </summary>
    [Fact]
    public async Task The_valueless_worktreeConfig_exploit_makes_the_next_create_refuse()
    {
        using var remote = LocalGitRemote.Create();
        var first = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var mirror = MirrorOf("sandbox");
        GitCliRunWorkspaceGitTests.PlantWorktreeConfigExploit(first.Value.Root, mirror, _temp);

        var second = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);

        second.IsFailure.Should().BeTrue("a valueless extensions.worktreeConfig is outside the allowed surface");
        IsIntactBareRepository(mirror).Should().BeTrue("a mirror refused for disallowed config must never be deleted");
    }

    /// <summary>
    /// Fix round 3, item 3: a single-valued change to <c>remote.origin.url</c> is silently corrected by the next
    /// create's own overwrite (see <c>MirrorConfigSurface</c>'s remarks on why the value check runs after that
    /// write, not before — a mirror reused for a genuinely different remote must still succeed). A <em>second</em>
    /// value, added rather than replacing the first, is what "changed" means here: git itself refuses to
    /// collapse an already-ambiguous, multi-valued key with a single plain <c>git config</c> write
    /// ("cannot overwrite multiple values with a single value"), so the next create's own
    /// <c>remote.origin.url</c> update fails outright, closed, before ever reaching a fetch.
    /// </summary>
    [Fact]
    public async Task A_second_remote_origin_url_value_makes_the_next_create_refuse()
    {
        using var remote = LocalGitRemote.Create();
        var first = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var mirror = MirrorOf("sandbox");
        LocalGitRemote.RunGit(mirror, "config", "--add", "remote.origin.url", "https://evil.example/redirect.git");

        var second = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        IsIntactBareRepository(mirror).Should().BeTrue("a mirror refused for disallowed config must never be deleted");
    }
}
