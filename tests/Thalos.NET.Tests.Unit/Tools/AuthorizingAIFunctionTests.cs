using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Tools;

public sealed class AuthorizingAIFunctionTests
{
    private static AIFunction Echo() => AIFunctionFactory.Create((string text) => $"echo:{text}", "echo", "Echoes text");

    private static (AuthorizingAIFunction fn, IToolAuthorizer auth, RecordingPublisher pub) Build(bool allow)
    {
        var auth = Substitute.For<IToolAuthorizer>();
        auth.AuthorizeAsync(Arg.Any<ISecurityContext>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(allow ? ToolAuthorizationDecision.Allow() : ToolAuthorizationDecision.Deny("nope"));
        var pub = new RecordingPublisher();
        return (new AuthorizingAIFunction(Echo(), "test__echo", auth, pub, TimeProvider.System), auth, pub);
    }

    [Fact]
    public void Exposes_qualified_name_but_keeps_description_and_schema()
    {
        var (fn, _, _) = Build(true);
        fn.Name.Should().Be("test__echo");
        fn.Description.Should().Be("Echoes text");
        fn.JsonSchema.GetProperty("properties").TryGetProperty("text", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Allowed_call_invokes_inner_and_publishes_requested_and_completed()
    {
        var (fn, auth, pub) = Build(true);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new Runtime.TestSecurityContext("u1"));

        var result = await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "hi" });

        result!.ToString().Should().Contain("echo:hi");
        await auth.Received(1).AuthorizeAsync(Arg.Is<ISecurityContext>(c => c.Id == "u1"), "test__echo", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        pub.Of<ToolCallRequestedNotification>().Should().ContainSingle(n => n.ToolName == "test__echo" && n.CallerId == "u1");
        pub.Of<ToolCallCompletedNotification>().Should().ContainSingle(n => n.Succeeded);
        scope.ToolCalls.Should().ContainSingle(c => c.Succeeded && c.ToolName == "test__echo");
        // started + finished (ChannelReader.Count is unsupported on the single-reader channel, so drain instead)
        scope.Events.TryRead(out var first).Should().BeTrue();
        first.Should().BeOfType<ToolCallStartedEvent>();
        scope.Events.TryRead(out var second).Should().BeTrue();
        second.Should().BeOfType<ToolCallFinishedEvent>();
        scope.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Denied_call_does_not_invoke_inner_and_returns_denial_text()
    {
        var (fn, _, pub) = Build(false);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var result = await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "hi" });

        result!.ToString().Should().Contain("denied").And.Contain("nope");
        pub.Of<ToolCallDeniedNotification>().Should().ContainSingle(n => n.Reason == "nope");
        pub.Of<ToolCallCompletedNotification>().Should().BeEmpty();
        scope.ToolCalls.Should().ContainSingle(c => !c.Succeeded);
    }

    [Fact]
    public async Task Inner_exception_is_reported_as_failed_call_and_rethrown()
    {
        var boom = AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("boom")), "boom");
        var auth = Substitute.For<IToolAuthorizer>();
        auth.AuthorizeAsync(default!, default!, default, default).ReturnsForAnyArgs(ToolAuthorizationDecision.Allow());
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(boom, "t__boom", auth, pub, TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var act = () => fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();
        pub.Of<ToolCallCompletedNotification>().Should().ContainSingle(n => !n.Succeeded);
    }

    [Fact]
    public async Task Outside_a_turn_scope_caller_is_anonymous_and_no_events_are_lost()
    {
        var (fn, auth, _) = Build(true);
        await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "x" });
        await auth.Received(1).AuthorizeAsync(Arg.Is<ISecurityContext>(c => c.Id == AnonymousSecurityContext.AnonymousId), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }
}
