using Thalos.Git.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>The lexical rules of <see cref="RepoRelativePath.Validate"/>, the same as WorkspacePath's lexical stage.</summary>
public sealed class RepoRelativePathTests
{
    /// <summary>Red: remove the matching rule from RepoRelativePath.Validate; the case then validates as acceptable.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/x")]
    [InlineData("a:b")]
    [InlineData("a/b:stream")]
    [InlineData("a\0b")]
    [InlineData("./a")]
    [InlineData("a/./b")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData(".git")]
    [InlineData("a/.GIT/config")]
    [InlineData("a\\..\\b")]
    public void A_path_the_rules_refuse_is_refused(string path) =>
        RepoRelativePath.Validate(path).Should().NotBeNull();

    /// <summary>Red: refuse every path, or any segment that merely starts with a dot; the case then fails.</summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("src/a/b.cs")]
    [InlineData(".github/workflows/x.yml")]
    [InlineData(".gitignore")]
    [InlineData("a..b/c")]
    public void An_ordinary_path_is_accepted(string path) =>
        RepoRelativePath.Validate(path).Should().BeNull();
}
