using Thalos.Mcp;

namespace Thalos.Tests.Sandbox;

/// <summary>The cleaning of sandbox-written text before RemoteRunToolSource logs it.</summary>
public sealed class LogSanitizerTests
{
    /// <summary>Red 1: return the text unchanged; the control characters and the full length then survive. Red 2: cut without the ellipsis; the cut is then invisible.</summary>
    [Fact]
    public void Control_characters_become_spaces_and_long_text_is_cut()
    {
        var forged = "unknown tool\r\n2026-10-02 INFO forged line\u001b[31m\u0000" + new string('x', 500);

        var clean = LogSanitizer.Clean(forged);

        clean.Should().NotContainAny("\r", "\n", "\u001b", "\u0000");
        clean.Should().StartWith("unknown tool  2026-10-02 INFO forged line [31m ");
        clean.Should().HaveLength(LogSanitizer.MaxLength + 1).And.EndWith("…");
        LogSanitizer.Clean("short").Should().Be("short");
        LogSanitizer.Clean(null).Should().BeEmpty();
    }
}
