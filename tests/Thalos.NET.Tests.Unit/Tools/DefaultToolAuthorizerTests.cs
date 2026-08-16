using System.Text.Json;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Tools;

public sealed class DefaultToolAuthorizerTests
{
    // internal (not private): the ZeroAlloc.Authorization source generator emits a DI registration for every
    // [Policy] type in the assembly and cannot reference a private nested type.
    [Policy("developer")]
    internal sealed class DeveloperPolicy : IAuthorizationPolicy
    {
        public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default) =>
            new(ctx.Roles.Contains("developer")
                ? UnitResult<AuthorizationFailure>.Success()
                : UnitResult<AuthorizationFailure>.Failure(new AuthorizationFailure("role", "developer role required")));
    }

    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;
    private static ISecurityContext Dev => new Runtime.TestSecurityContext("u", "developer");
    private static ISecurityContext Guest => new Runtime.TestSecurityContext("g");

    [Fact]
    public async Task No_bindings_allows_everything()
    {
        var auth = new DefaultToolAuthorizer([], []);
        (await auth.AuthorizeAsync(Guest, "roslyn__apply_code_action", NoArgs, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Binding_denies_when_policy_fails_and_allows_when_it_passes()
    {
        var auth = new DefaultToolAuthorizer([new ToolPolicyBinding("roslyn__apply_*", "developer")], [new DeveloperPolicy()]);

        var denied = await auth.AuthorizeAsync(Guest, "roslyn__apply_code_action", NoArgs, default);
        denied.Allowed.Should().BeFalse();
        denied.Reason.Should().Contain("developer role required");

        (await auth.AuthorizeAsync(Dev, "roslyn__apply_code_action", NoArgs, default)).Allowed.Should().BeTrue();
        (await auth.AuthorizeAsync(Guest, "roslyn__find_callers", NoArgs, default)).Allowed.Should().BeTrue("unbound tools are allowed");
    }

    [Fact]
    public async Task Missing_policy_denies_closed()
    {
        var auth = new DefaultToolAuthorizer([new ToolPolicyBinding("*", "does-not-exist")], []);
        var d = await auth.AuthorizeAsync(Dev, "x__y", NoArgs, default);
        d.Allowed.Should().BeFalse();
        d.Reason.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task All_matching_bindings_must_pass()
    {
        var auth = new DefaultToolAuthorizer(
            [new ToolPolicyBinding("*", "developer"), new ToolPolicyBinding("roslyn__*", "missing")],
            [new DeveloperPolicy()]);
        (await auth.AuthorizeAsync(Dev, "roslyn__x", NoArgs, default)).Allowed.Should().BeFalse();
        (await auth.AuthorizeAsync(Dev, "other__x", NoArgs, default)).Allowed.Should().BeTrue();
    }
}
