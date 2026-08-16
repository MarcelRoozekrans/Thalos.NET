using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using ZeroAlloc.Mediator;

namespace Thalos.Tests.Unit.Tools;

public sealed class AuthorizingAIFunctionTests
{
    private static AIFunction Echo() => AIFunctionFactory.Create((string text) => $"echo:{text}", "echo", "Echoes text");

    private static AIFunctionArguments Args(string text) => new(StringComparer.Ordinal) { ["text"] = text };

    private static IToolAuthorizer Allowing()
    {
        var auth = Substitute.For<IToolAuthorizer>();
        auth.AuthorizeAsync(default!, default!, default, default).ReturnsForAnyArgs(ToolAuthorizationDecision.Allow());
        return auth;
    }

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

        var result = await fn.InvokeAsync(Args("hi"));

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
    public async Task Arguments_are_serialized_compactly()
    {
        var (fn, _, pub) = Build(true);
        await fn.InvokeAsync(Args("hi"));
        pub.Of<ToolCallRequestedNotification>().Should().ContainSingle().Which.ArgumentsJson.Should().Be("""{"text":"hi"}""");
    }

    [Fact]
    public async Task Denied_call_does_not_invoke_inner_and_returns_denial_text()
    {
        var (fn, _, pub) = Build(false);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var result = await fn.InvokeAsync(Args("hi"));

        result!.ToString().Should().Contain("denied").And.Contain("nope");
        pub.Of<ToolCallDeniedNotification>().Should().ContainSingle(n => n.Reason == "nope");
        pub.Of<ToolCallCompletedNotification>().Should().BeEmpty();
        scope.ToolCalls.Should().ContainSingle(c => !c.Succeeded);
    }

    [Fact]
    public async Task Authorizer_exception_is_treated_as_denial()
    {
        var invoked = false;
        var tool = AIFunctionFactory.Create(() => { invoked = true; return "ran"; }, "t");
        var auth = Substitute.For<IToolAuthorizer>();
        auth.AuthorizeAsync(default!, default!, default, default)
            .ReturnsForAnyArgs<ToolAuthorizationDecision>(_ => throw new InvalidOperationException("policy store down"));
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(tool, "x__t", auth, pub, TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var result = await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));

        invoked.Should().BeFalse();
        result!.ToString().Should().Be("Tool call denied: authorization error");
        pub.Of<ToolCallDeniedNotification>().Should().ContainSingle(n => n.Reason == "authorization error");
        pub.Of<ToolCallCompletedNotification>().Should().BeEmpty();
        scope.ToolCalls.Should().ContainSingle(c => !c.Succeeded);
    }

    [Fact]
    public async Task Publisher_exception_propagates_and_tool_is_not_invoked()
    {
        var invoked = false;
        var tool = AIFunctionFactory.Create(() => { invoked = true; return "ran"; }, "t");
        var fn = new AuthorizingAIFunction(tool, "x__t", Allowing(), new ThrowingPublisher(), TimeProvider.System);

        var act = () => fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("bus down");
        invoked.Should().BeFalse();
    }

    [Fact]
    public async Task Inner_exception_is_reported_as_failed_call_and_rethrown()
    {
        var boom = AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("boom")), "boom");
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(boom, "t__boom", Allowing(), pub, TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var act = () => fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();
        pub.Of<ToolCallCompletedNotification>().Should().ContainSingle(n => !n.Succeeded);
        scope.ToolCalls.Should().ContainSingle(c => !c.Succeeded && c.ResultPreview == "InvalidOperationException");
    }

    [Fact]
    public async Task Tool_internal_cancellation_is_audited_and_rethrown()
    {
        var timeout = AIFunctionFactory.Create(new Func<string>(() => throw new OperationCanceledException("tool timeout")), "slow");
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(timeout, "t__slow", Allowing(), pub, TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var act = () => fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        pub.Of<ToolCallCompletedNotification>().Should().ContainSingle(n => !n.Succeeded);
        scope.ToolCalls.Should().ContainSingle(c => !c.Succeeded && c.ResultPreview == "OperationCanceledException");
    }

    [Fact]
    public async Task Ambient_cancellation_after_start_does_not_suppress_the_audit_of_a_failing_tool()
    {
        using var cts = new CancellationTokenSource();
        var tool = AIFunctionFactory.Create(new Func<string>(() =>
        {
            cts.Cancel(); // ambient token is cancelled while the tool runs, then the tool fails for its own reason
            throw new InvalidOperationException("boom");
        }), "t");
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(tool, "x__t", Allowing(), pub, TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var act = () => fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal), cts.Token).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        pub.Of<ToolCallCompletedNotification>().Should().ContainSingle(n => !n.Succeeded);
        scope.ToolCalls.Should().ContainSingle(c => !c.Succeeded && c.ResultPreview == "InvalidOperationException");
        scope.Events.TryRead(out _).Should().BeTrue();
        scope.Events.TryRead(out var finished).Should().BeTrue();
        finished.Should().BeOfType<ToolCallFinishedEvent>().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Ambient_cancellation_thrown_by_the_tool_is_not_audited_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var tool = AIFunctionFactory.Create(new Func<string>(() =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }), "t");
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(tool, "x__t", Allowing(), pub, TimeProvider.System);

        var act = () => fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal), cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        pub.Of<ToolCallCompletedNotification>().Should().BeEmpty();
    }

    /// <summary>AIFunctionFactory marshals return values to JsonElement; a string result must preview as the string, not as <c>"\"…\""</c>.</summary>
    [Fact]
    public async Task String_result_marshalled_as_JsonElement_previews_as_the_plain_string()
    {
        var (fn, _, _) = Build(true);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        var result = await fn.InvokeAsync(Args("hi"));

        result.Should().BeOfType<JsonElement>().Which.ValueKind.Should().Be(JsonValueKind.String);
        scope.ToolCalls.Should().ContainSingle().Which.ResultPreview.Should().Be("echo:hi");
    }

    [Fact]
    public async Task Object_result_previews_as_its_json()
    {
        var tool = AIFunctionFactory.Create(() => new { ok = true }, "obj");
        var fn = new AuthorizingAIFunction(tool, "t__obj", Allowing(), new RecordingPublisher(), TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));

        // raw JSON (AIJsonUtilities.DefaultOptions writes indented) — only strings are unwrapped
        var preview = scope.ToolCalls.Should().ContainSingle().Which.ResultPreview;
        preview.Should().StartWith("{").And.EndWith("}").And.Contain("\"ok\": true");
    }

    [Fact]
    public async Task Long_results_are_previewed_to_200_chars_plus_ellipsis()
    {
        var payload = new string('x', 250);
        var tool = AIFunctionFactory.Create(() => payload, "big");
        var pub = new RecordingPublisher();
        var fn = new AuthorizingAIFunction(tool, "t__big", Allowing(), pub, TimeProvider.System);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));

        var preview = scope.ToolCalls.Should().ContainSingle().Which.ResultPreview;
        preview.Should().NotBeNull();
        preview!.Length.Should().Be(201);
        preview.Should().EndWith("…");
    }

    [Fact]
    public async Task Outside_a_turn_scope_caller_is_anonymous_and_no_events_are_lost()
    {
        var (fn, auth, _) = Build(true);
        await fn.InvokeAsync(Args("x"));
        await auth.Received(1).AuthorizeAsync(Arg.Is<ISecurityContext>(c => c.Id == AnonymousSecurityContext.AnonymousId), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    private sealed class ThrowingPublisher : IAgentNotificationPublisher
    {
        public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification =>
            throw new InvalidOperationException("bus down");
    }
}
