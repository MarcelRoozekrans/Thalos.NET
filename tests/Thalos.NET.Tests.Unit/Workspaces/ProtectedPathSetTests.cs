using Thalos.Workspaces;

namespace Thalos.Tests.Unit.Workspaces;

public sealed class ProtectedPathSetTests
{
    private static readonly ProtectedPathSet Set = new([".github/", "AGENT.md", ".gitattributes", "docs/ci/"]);

    /// <summary>Red: treat a trailing '/' entry as an exact match.</summary>
    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".GitHub/Workflows/x.yml")]
    [InlineData("docs/ci/a.md")]
    public void A_path_under_a_directory_entry_is_protected(string path) => Set.IsProtected(path).Should().BeTrue();

    /// <summary>Red: do not replace backslashes with '/' in the input.</summary>
    [Fact]
    public void A_backslash_path_is_read_as_a_slash_path() => Set.IsProtected(@".github\workflows\ci.yml").Should().BeTrue();

    /// <summary>Red: compare files case-sensitively.</summary>
    [Theory]
    [InlineData("AGENT.md")]
    [InlineData("agent.md")]
    [InlineData(".gitattributes")]
    public void An_exact_entry_is_protected(string path) => Set.IsProtected(path).Should().BeTrue();

    /// <summary>Red: match directories without requiring the separator after them, so ".githubx" matches.</summary>
    [Theory]
    [InlineData(".githubx/a.yml")]
    [InlineData("docs/cia/a.md")]
    public void A_directory_lookalike_is_not_protected(string path) => Set.IsProtected(path).Should().BeFalse();

    /// <summary>Red: match directories by Contains rather than from the start of the path.</summary>
    [Fact]
    public void A_directory_entry_does_not_match_below_another_directory() => Set.IsProtected("src/.github/x.yml").Should().BeFalse();

    /// <summary>Red: match files by StartsWith.</summary>
    [Fact]
    public void A_file_entry_does_not_match_a_longer_name() => Set.IsProtected("AGENT.md.bak").Should().BeFalse();

    /// <summary>Red: match files by file name only, or by EndsWith.</summary>
    [Fact]
    public void A_file_entry_does_not_match_the_same_name_in_a_subdirectory() => Set.IsProtected("docs/AGENT.md").Should().BeFalse();

    /// <summary>The directory itself, as git reports it. Red: require the trailing slash on input.</summary>
    [Fact]
    public void The_directory_entry_matches_the_bare_directory_name() => Set.IsProtected(".github").Should().BeTrue();

    /// <summary>Red: replace "//" once rather than dropping every empty segment, or skip dropping "." segments.</summary>
    [Theory]
    [InlineData("docs///ci/a.md")]
    [InlineData("./.github/x.yml")]
    [InlineData("docs/./ci/a.md")]
    [InlineData("/.github/x.yml")]
    [InlineData(@"docs\.\ci\a.md")]
    [InlineData("./AGENT.md")]
    public void A_non_canonical_spelling_is_still_protected(string path) => Set.IsProtected(path).Should().BeTrue();

    /// <summary>Red: drop the ".." check, which leaves such a path unmatched.</summary>
    [Theory]
    [InlineData("src/../.github/x.yml")]
    [InlineData("../x")]
    [InlineData(@"a\..\b")]
    public void A_path_with_a_parent_segment_fails_closed(string path) => Set.IsProtected(path).Should().BeTrue();

    /// <summary>Red: skip the ".." check when normalising an entry.</summary>
    [Fact]
    public void An_entry_with_a_parent_segment_is_refused() =>
        FluentActions.Invoking(() => new ProtectedPathSet(["docs/../x/"])).Should().Throw<ArgumentException>();

    /// <summary>Red: normalise entries with a single Replace, or with only a leading-slash trim.</summary>
    [Fact]
    public void Entries_are_normalised_like_paths()
    {
        var set = new ProtectedPathSet([@"\.github\", "./AGENT.md", "docs//ci/"]);

        set.Entries.Should().Equal(".github/", "AGENT.md", "docs/ci/");
        set.IsProtected(".github/x").Should().BeTrue();
        set.IsProtected("docs/ci/x").Should().BeTrue();
    }

    /// <summary>Red: keep empty or whitespace entries, so "" or "/" protects everything or nothing.</summary>
    [Fact]
    public void Empty_and_whitespace_entries_are_ignored()
    {
        var set = new ProtectedPathSet(["", " ", "/", ".", "AGENT.md"]);

        set.Entries.Should().Equal("AGENT.md");
        set.IsProtected("src/a.cs").Should().BeFalse();
    }
}
