using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class MarkdownV2EscaperTests
{
    [Theory]
    [InlineData("_", "\\_")]
    [InlineData("*", "\\*")]
    [InlineData("[", "\\[")]
    [InlineData("]", "\\]")]
    [InlineData("(", "\\(")]
    [InlineData(")", "\\)")]
    [InlineData("~", "\\~")]
    [InlineData("`", "\\`")]
    [InlineData(">", "\\>")]
    [InlineData("#", "\\#")]
    [InlineData("+", "\\+")]
    [InlineData("-", "\\-")]
    [InlineData("=", "\\=")]
    [InlineData("|", "\\|")]
    [InlineData("{", "\\{")]
    [InlineData("}", "\\}")]
    [InlineData(".", "\\.")]
    [InlineData("!", "\\!")]
    public void Every_reserved_character_is_escaped(string input, string expected)
    {
        // The brief's own character list, and the Bot API's "in all other places" rule, both include the backtick
        // among the escaped characters — added here since a lone backtick (not part of a ``` fence) is exactly the
        // sort of case a triple-backtick-focused implementation could special-case away by mistake.
        MarkdownV2Escaper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void Prose_with_punctuation_survives()
    {
        MarkdownV2Escaper.Escape("Done. Check HttpSecurityContextFactory.TryCreate!")
            .Should().Be("Done\\. Check HttpSecurityContextFactory\\.TryCreate\\!");
    }

    [Fact]
    public void A_fenced_code_block_keeps_its_fence_and_its_contents_unescaped_while_text_around_it_is_still_escaped()
    {
        // Strengthened from the brief's Contain/StartWith/EndWith trio to a single full-output assertion: with the
        // brief's original "before"/"after" wrapper text (no reserved characters outside the fence), a no-op
        // escaper would also satisfy Contain/StartWith/EndWith. Adding "." and "!" outside the fence, and asserting
        // the exact result, proves both halves of the contract — fence content untouched, surrounding text escaped
        // — in one assertion that a do-nothing implementation cannot pass.
        const string input = "before.\n```csharp\nvar x = a.b(1);\n```\nafter!";
        const string expected = "before\\.\n```csharp\nvar x = a.b(1);\n```\nafter\\!";

        MarkdownV2Escaper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void An_unclosed_fence_is_closed_so_the_message_still_parses()
    {
        // An agent whose output was truncated mid-fence would otherwise produce a 400 for the whole message.
        // Strengthened from EndWith("```") to a full-output assertion, since the exact result is easy to state and
        // EndWith alone would not catch a bug that mangled the text preceding the appended fence.
        const string input = "text\n```csharp\nvar x = 1;";
        const string expected = "text\n```csharp\nvar x = 1;```";

        MarkdownV2Escaper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void A_backslash_inside_a_code_block_is_escaped_because_Telegram_requires_it()
    {
        // Strengthened from Contain to a full-output assertion covering the whole fenced block.
        const string input = "```\nC:\\temp\n```";
        const string expected = "```\nC:\\\\temp\n```";

        MarkdownV2Escaper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void Empty_input_is_returned_unchanged()
    {
        MarkdownV2Escaper.Escape(string.Empty).Should().BeEmpty();
    }
}
