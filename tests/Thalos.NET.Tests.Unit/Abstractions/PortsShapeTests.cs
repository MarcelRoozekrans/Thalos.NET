using System.Reflection;
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
            "IChatClientProvider", "IChatClientDecorator", "IToolAuthorizer", "IAgentNotificationPublisher", "IChannelAdapter"]);
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
            t.IsAssignableTo(typeof(INotification)).Should().BeTrue();
        });
    }
}
