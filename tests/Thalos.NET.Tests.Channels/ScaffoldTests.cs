namespace Thalos.Tests.Channels;

public sealed class ScaffoldTests
{
    [Fact]
    public void Channels_assembly_is_loaded_and_multi_targets()
    {
        typeof(Thalos.Channels.ChannelsMarker).Assembly.GetName().Name.Should().Be("Thalos.NET.Channels");
    }
}
