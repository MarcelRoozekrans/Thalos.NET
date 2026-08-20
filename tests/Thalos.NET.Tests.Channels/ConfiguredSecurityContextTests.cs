using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ConfiguredSecurityContextTests
{
    [Fact]
    public void Id_and_roles_come_from_configuration()
    {
        var ctx = new ConfiguredSecurityContext("telegram:marcel", ["admin"]);
        ctx.Id.Should().Be("telegram:marcel");
        ctx.Roles.Should().BeEquivalentTo(["admin"]);
    }

    [Fact]
    public void An_empty_role_set_is_the_read_only_default_and_is_never_null()
    {
        var ctx = new ConfiguredSecurityContext("telegram:marcel", []);
        ctx.Roles.Should().BeEmpty();
        ctx.Claims.Should().NotBeNull();
    }

    [Fact]
    public void Roles_compare_ordinally_so_Developer_does_not_satisfy_developer()
    {
        // DeveloperPolicy does a plain Contains; a case-insensitive set here would silently grant the mutating tools.
        new ConfiguredSecurityContext("x", ["Developer"]).Roles.Contains("developer").Should().BeFalse();
    }

    [Fact]
    public void A_blank_id_is_rejected_because_session_ownership_is_keyed_on_it()
    {
        var act = () => new ConfiguredSecurityContext("  ", []);
        act.Should().Throw<ArgumentException>();
    }
}
