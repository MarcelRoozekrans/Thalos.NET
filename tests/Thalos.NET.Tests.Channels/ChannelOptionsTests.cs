using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class ChannelOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "daedalus" }).Should().BeNull();
    }

    [Fact]
    public void Section_name_is_the_documented_one()
    {
        ChannelOptions.SectionName.Should().Be("Thalos:Channels");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultAgent_must_be_present(string? agent)
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = agent! })
            .Should().Contain("DefaultAgent");
    }

    [Fact]
    public void IdleTimeout_must_be_positive()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "a", IdleTimeout = TimeSpan.Zero })
            .Should().Contain("IdleTimeout");
    }

    [Fact]
    public void FlushInterval_may_be_zero_to_render_every_delta()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "a", FlushInterval = TimeSpan.Zero })
            .Should().BeNull();
    }

    [Fact]
    public void FlushInterval_must_not_be_negative()
    {
        ChannelOptions.Describe(new ChannelOptions { DefaultAgent = "a", FlushInterval = TimeSpan.FromSeconds(-1) })
            .Should().Contain("FlushInterval");
    }
}
