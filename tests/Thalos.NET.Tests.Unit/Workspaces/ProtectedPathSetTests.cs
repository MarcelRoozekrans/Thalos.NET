using Thalos.Workspaces;

namespace Thalos.Tests.Unit.Workspaces;

public sealed class ProtectedPathSetTests
{
    private static readonly ProtectedPathSet Set = new([".github/", "AGENT.md", ".gitattributes", "docs/ci/"]);

    /// <summary>Red: treat a trailing '/' entry as an exact match.</summary>
    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".GitHub/Workflows/x.yml")]
    [InlineData(@".github\workflows\ci.yml")]
    [InlineData("docs/ci/a.md")]
    public void A_path_under_a_directory_entry_is_protected(string path) => Set.IsProtected(path).Should().BeTrue();

    /// <summary>Red: compare case-sensitively, or forget to normalise backslashes.</summary>
    [Theory]
    [InlineData("AGENT.md")]
    [InlineData("agent.md")]
    [InlineData(".gitattributes")]
    public void An_exact_entry_is_protected(string path) => Set.IsProtected(path).Should().BeTrue();

    /// <summary>Red: use StartsWith without the separator, so ".githubx" or "AGENT.md.bak" match.</summary>
    [Theory]
    [InlineData(".githubx/a.yml")]
    [InlineData("src/.github/x.yml")]
    [InlineData("AGENT.md.bak")]
    [InlineData("docs/AGENT.md")]
    [InlineData("docs/cia/a.md")]
    public void Lookalikes_are_not_protected(string path) => Set.IsProtected(path).Should().BeFalse();

    /// <summary>The directory itself, as git reports it. Red: require the trailing slash on input.</summary>
    [Fact]
    public void The_directory_entry_matches_the_bare_directory_name() => Set.IsProtected(".github").Should().BeTrue();
}
