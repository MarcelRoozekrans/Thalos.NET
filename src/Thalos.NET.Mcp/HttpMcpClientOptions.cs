using ModelContextProtocol.Client;

namespace Thalos.Mcp;

/// <summary>The MCP client options for a client over Streamable HTTP.</summary>
internal static class HttpMcpClientOptions
{
    /// <summary>
    /// Options whose <c>server/discover</c> probe has no timeout of its own, so the caller's connect bound alone limits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK's default probe timeout of 5 seconds exists for servers that drop an unknown method without answering, as
    /// a stdio server may. An HTTP server always answers the POST, so a server that predates <c>server/discover</c> is
    /// still recognised at once by its error answer. Only a slow server reaches the timeout, and the SDK then takes it
    /// for an old one and falls back to <c>initialize</c>.
    /// </para>
    /// <para>
    /// That fallback can fail outright. When the probe's answer has been read but not yet handed to the waiting request
    /// as the timeout fires, the HTTP transport keeps the probe's protocol version. It then sends <c>initialize</c> with a
    /// <c>MCP-Protocol-Version</c> header that does not match its body, and the server refuses it with 400. A server that
    /// took about 5 seconds to answer its first request, such as a cold or loaded sandbox host, therefore failed the
    /// connection well within the caller's connect timeout.
    /// </para>
    /// </remarks>
    public static McpClientOptions Create() => new() { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };
}
