using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramOptionsTests
{
    private static TelegramOptions Valid() => new()
    {
        BotToken = "T",
        AllowedUserIds = [7],
        PrincipalId = "telegram:marcel",
    };

    [Fact]
    public void A_fully_configured_instance_is_valid()
    {
        TelegramOptions.Describe(Valid()).Should().BeNull();
    }

    [Fact]
    public void Section_name_is_the_documented_one()
    {
        TelegramOptions.SectionName.Should().Be("Thalos:Channels:Telegram");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BotToken_must_be_present(string? token)
    {
        var options = Valid();
        options.BotToken = token!;

        TelegramOptions.Describe(options).Should().Contain("BotToken");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PrincipalId_must_be_present(string? principalId)
    {
        var options = Valid();
        options.PrincipalId = principalId!;

        TelegramOptions.Describe(options).Should().Contain("PrincipalId");
    }

    /// <summary>
    /// The one misconfiguration the whole channel's security depends on rejecting: an empty allow-list must never
    /// be read as "allow everyone". A non-dropping implementation (one that treated an empty list as permissive)
    /// would pass every OTHER assertion in this suite yet leave the agent open to any sender who finds the bot —
    /// this is the one test that would actually fail against that implementation.
    /// </summary>
    [Fact]
    public void An_empty_allow_list_is_a_validation_failure_not_a_permissive_default()
    {
        var options = Valid();
        options.AllowedUserIds = [];

        TelegramOptions.Describe(options).Should().Contain("AllowedUserIds");
    }

    [Fact]
    public void A_non_empty_allow_list_is_accepted()
    {
        var options = Valid();
        options.AllowedUserIds = [7, 42];

        TelegramOptions.Describe(options).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PollTimeoutSeconds_must_be_positive(int seconds)
    {
        var options = Valid();
        options.PollTimeoutSeconds = seconds;

        TelegramOptions.Describe(options).Should().Contain("PollTimeoutSeconds");
    }

    [Fact]
    public void Defaults_are_all_documented_values()
    {
        var options = new TelegramOptions();

        options.Enabled.Should().BeTrue();
        options.BotToken.Should().BeEmpty();
        options.AllowedUserIds.Should().BeEmpty();
        options.PrincipalId.Should().BeEmpty();
        options.Roles.Should().BeEmpty();
        options.PollTimeoutSeconds.Should().Be(50);
    }
}
