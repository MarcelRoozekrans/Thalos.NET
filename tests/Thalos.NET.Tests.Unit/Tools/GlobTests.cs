using Thalos.Tools;

namespace Thalos.Tests.Unit.Tools;

public sealed class GlobTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("roslyn__*", "roslyn__find_callers", true)]
    [InlineData("roslyn__*", "memorylens__snapshot", false)]
    [InlineData("roslyn__find_?allers", "roslyn__find_callers", true)]
    [InlineData("roslyn__apply_*", "roslyn__apply_code_action", true)]
    [InlineData("roslyn__apply_*", "roslyn__find_callers", false)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    [InlineData("*_action", "roslyn__apply_code_action", true)]
    public void Matches(string pattern, string input, bool expected)
    {
        Glob.IsMatch(pattern, input).Should().Be(expected);
    }

    [Fact]
    public void Matching_is_case_sensitive()
    {
        Glob.IsMatch("Roslyn__*", "roslyn__x").Should().BeFalse();
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("*", "", true)]
    [InlineData("**", "abc", true)]
    [InlineData("a*b*c", "abc", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("?", "", false)]
    [InlineData("a?", "a", false)]
    [InlineData("*a", "ba", true)]
    public void Edge_cases(string pattern, string input, bool expected)
    {
        Glob.IsMatch(pattern, input).Should().Be(expected);
    }

    [Fact]
    public void Pathological_pattern_is_linear_not_exponential()
    {
        var pattern = "*a*a*a*a*a*a*a*a*a*a*b";
        var input = new string('a', 64);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var matched = Glob.IsMatch(pattern, input);
        sw.Stop();

        matched.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50));
    }
}
