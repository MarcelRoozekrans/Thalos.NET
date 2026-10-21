using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Tools;

/// <summary>
/// The arguments text <see cref="AuthorizingAIFunction"/> publishes and hands its authorizer, for arguments of every
/// shape a model or a host passes, captured before the call moves off reflection-based JSON. A characterization test:
/// it passes on today's code.
/// </summary>
public sealed class AuthorizingAIFunctionGoldenTests
{
    // Red: in AuthorizingAIFunction, build CompactJson with WriteIndented = true; the published text gains line breaks.
    [Fact]
    public async Task Arguments_of_every_shape_are_published_and_authorized_as_before()
    {
        JsonElement? authorized = null;
        var authorizer = Substitute.For<IToolAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<ISecurityContext>(), Arg.Any<string>(), Arg.Do<JsonElement>(e => authorized = e.Clone()), Arg.Any<CancellationToken>())
            .Returns(ToolAuthorizationDecision.Allow());
        var publisher = new RecordingNotificationPublisher();
        var inner = AIFunctionFactory.Create((string text) => text, "echo", "Echoes text");
        var function = new AuthorizingAIFunction(inner, "test__echo", authorizer, publisher, TimeProvider.System);
        var arguments = new AIFunctionArguments(StringComparer.Ordinal)
        {
            ["text"] = "a\"b <a & 'é' 🚀>\nline",
            ["count"] = 3,
            ["big"] = 9_000_000_000L,
            ["ratio"] = 1.5,
            ["flag"] = true,
            ["none"] = null,
            ["list"] = new[] { "x", "y" },
            ["map"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v", ["n"] = 2 },
            ["element"] = JsonDocument.Parse("""{"x":[1,2],"y":"z"}""").RootElement.Clone(),
        };

        await function.InvokeAsync(arguments);

        var published = publisher.Of<ToolCallRequestedNotification>().Should().ContainSingle().Subject.ArgumentsJson;
        published.Should().Be("""
            {"text":"a\"b <a & 'é' \uD83D\uDE80>\nline","count":3,"big":9000000000,"ratio":1.5,"flag":true,"none":null,"list":["x","y"],"map":{"k":"v","n":2},"element":{"x":[1,2],"y":"z"}}
            """);
        authorized!.Value.GetRawText().Should().Be(published);
    }
}
