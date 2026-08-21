using System.Reflection;
using System.Runtime.CompilerServices;
using ZeroAlloc.Mediator;

namespace Thalos.Tests.Unit.Abstractions;

public sealed class PortsShapeTests
{
    [Fact]
    public void All_ports_are_interfaces_in_Thalos_namespace()
    {
        var ports = typeof(IAgentRuntime).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Name.StartsWith('I') && string.Equals(t.Namespace, "Thalos", StringComparison.Ordinal))
            .Select(t => t.Name).ToArray();

        ports.Should().Contain(["IAgentRuntime", "IAgentSessionStore", "IAgentCatalog", "IToolSource",
            "IChatClientProvider", "IChatClientDecorator", "IToolAuthorizer", "IAgentNotificationPublisher", "IChannelAdapter",
            "IChannelSource"]);
    }

    [Fact]
    public void All_notifications_are_readonly_record_structs_implementing_INotification()
    {
        var notifications = typeof(IAgentRuntime).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Notification", StringComparison.Ordinal)).ToArray();

        notifications.Should().NotBeEmpty();
        notifications.Should().AllSatisfy(t =>
        {
            t.IsValueType.Should().BeTrue();
            t.IsDefined(typeof(IsReadOnlyAttribute), inherit: false).Should().BeTrue("{0} must be a readonly struct", t.Name);
            t.IsAssignableTo(typeof(INotification)).Should().BeTrue();
        });
    }

    [Fact]
    public void Generated_session_store_telemetry_proxy_is_public()
    {
        typeof(AgentSessionStoreInstrumented).IsPublic.Should().BeTrue();
        typeof(AgentSessionStoreInstrumented).Should().Implement<IAgentSessionStore>();
    }

    /// <summary>
    /// Pins the actual method shape of <see cref="IChannelAdapter"/>, not just its name. The test above already pins
    /// the set of port names, and that pin passed happily while <c>DeliverAsync</c> was keyed on <see cref="SessionId"/>
    /// instead of <see cref="ConversationId"/> — a defect that shipped in 0.3.0 and silently dropped every operator
    /// notice on Telegram. A name-only pin cannot catch a wrong parameter type; this one asserts parameter types,
    /// order and names, and the return type, so a future change to the signature fails this test even though the
    /// interface still compiles.
    /// </summary>
    [Fact]
    public void IChannelAdapter_method_shape_is_pinned()
    {
        var type = typeof(IChannelAdapter);

        var channelId = type.GetProperty(nameof(IChannelAdapter.ChannelId));
        channelId.Should().NotBeNull();
        channelId!.PropertyType.Should().Be<string>();
        channelId.CanRead.Should().BeTrue();
        channelId.CanWrite.Should().BeFalse();

        var deliverAsync = type.GetMethod(nameof(IChannelAdapter.DeliverAsync));
        deliverAsync.Should().NotBeNull();
        deliverAsync!.ReturnType.Should().Be<ValueTask>();

        var parameters = deliverAsync.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be<ConversationId>(
            "DeliverAsync must be keyed on the conversation, not the session — a SessionId here is the 0.3.0 defect this test exists to catch");
        parameters[0].Name.Should().Be("conversationId");
        parameters[1].ParameterType.Should().Be<AgentEvent>();
        parameters[1].Name.Should().Be("agentEvent");
        parameters[2].ParameterType.Should().Be<CancellationToken>();
        parameters[2].Name.Should().Be("ct");
    }

    /// <summary>Companion to the <c>IChannelAdapter</c> shape pin above, for the inbound side of the seam.</summary>
    [Fact]
    public void IChannelSource_method_shape_is_pinned()
    {
        var type = typeof(IChannelSource);

        var channelId = type.GetProperty(nameof(IChannelSource.ChannelId));
        channelId.Should().NotBeNull();
        channelId!.PropertyType.Should().Be<string>();
        channelId.CanRead.Should().BeTrue();
        channelId.CanWrite.Should().BeFalse();

        var readAsync = type.GetMethod(nameof(IChannelSource.ReadAsync));
        readAsync.Should().NotBeNull();
        readAsync!.ReturnType.Should().Be<IAsyncEnumerable<InboundMessage>>();

        var parameters = readAsync.GetParameters();
        parameters.Should().HaveCount(1);
        parameters[0].ParameterType.Should().Be<CancellationToken>();
        parameters[0].Name.Should().Be("ct");
    }
}
