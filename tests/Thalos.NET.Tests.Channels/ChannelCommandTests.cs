using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ChannelCommandTests
{
    [Theory]
    [InlineData("/new", ChannelCommandKind.New)]
    [InlineData("/end", ChannelCommandKind.End)]
    [InlineData("/status", ChannelCommandKind.Status)]
    [InlineData("/agents", ChannelCommandKind.Agents)]
    [InlineData("/cancel", ChannelCommandKind.Cancel)]
    [InlineData("/help", ChannelCommandKind.Help)]
    public void Known_commands_parse(string text, ChannelCommandKind expected)
    {
        ChannelCommand.Parse(text).Kind.Should().Be(expected);
    }

    [Fact]
    public void Commands_are_case_insensitive_and_tolerate_surrounding_space()
    {
        ChannelCommand.Parse("  /NEW  ").Kind.Should().Be(ChannelCommandKind.New);
    }

    [Fact]
    public void New_captures_its_argument()
    {
        var command = ChannelCommand.Parse("/new reviewer");
        command.Kind.Should().Be(ChannelCommandKind.New);
        command.Argument.Should().Be("reviewer");
    }

    [Fact]
    public void New_without_an_argument_has_a_null_argument()
    {
        ChannelCommand.Parse("/new").Argument.Should().BeNull();
    }

    [Fact]
    public void Telegram_bot_suffix_is_stripped()
    {
        // Telegram appends @botname to commands sent in any chat where the bot is not the only recipient.
        ChannelCommand.Parse("/new@daedalus_bot reviewer").Kind.Should().Be(ChannelCommandKind.New);
        ChannelCommand.Parse("/new@daedalus_bot reviewer").Argument.Should().Be("reviewer");
    }

    [Fact]
    public void Plain_text_is_not_a_command()
    {
        ChannelCommand.Parse("what changed in the auth layer?").Kind.Should().Be(ChannelCommandKind.None);
    }

    [Fact]
    public void A_slash_prefixed_word_that_is_not_a_command_is_Unknown_not_text()
    {
        // Treating it as text would silently send "/reboot" to the model as a prompt.
        ChannelCommand.Parse("/reboot").Kind.Should().Be(ChannelCommandKind.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_text_is_not_a_command(string? text)
    {
        ChannelCommand.Parse(text!).Kind.Should().Be(ChannelCommandKind.None);
    }
}
