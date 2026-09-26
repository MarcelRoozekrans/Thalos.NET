using Thalos.Git.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Scope rules of <see cref="MirrorConfigSurface.FindDisallowedKey"/>, on listings shaped exactly as
/// <c>git config --list --name-only --show-scope -z</c> prints them through <see cref="GitCli"/>.
/// </summary>
/// <remarks>
/// Worktree scope cannot be reached through real git alone: git reads <c>config.worktree</c> only when
/// <c>extensions.worktreeConfig</c> is set in the shared config, and that key is refused in local scope first. So the
/// worktree-scope rule is tested here on its own, with an allow-listed key that local scope would accept.
/// </remarks>
public sealed class MirrorConfigSurfaceTests
{
    [Fact]
    public void Any_key_in_worktree_scope_is_refused()
    {
        var violation = MirrorConfigSurface.FindDisallowedKey("local\0core.bare\0worktree\0core.bare\0\n");

        violation.Should().NotBeNull("the provider never writes a config.worktree, so no key may come from one, not even an allow-listed one");
    }

    [Fact]
    public void Allow_listed_local_keys_and_command_scope_pins_are_accepted()
    {
        var violation = MirrorConfigSurface.FindDisallowedKey("local\0core.bare\0local\0core.symlinks\0command\0core.hookspath\0\n");

        violation.Should().BeNull();
    }
}
