using ModelContextProtocol.Client;

namespace Thalos.Mcp;

/// <summary>The MCP client options for a client over Streamable HTTP.</summary>
internal static class HttpMcpClientOptions
{
    /// <summary>
    /// Options with no handshake timeout of the SDK's own, neither the <c>server/discover</c> probe's nor the whole
    /// initialization's, so the caller's connect token is the only bound on the handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK's default probe timeout of 5 seconds exists for servers that drop an unknown method without answering, as
    /// a stdio server may. A JSON-RPC server answers an unknown method with an error, so a server that predates
    /// <c>server/discover</c> is still recognised at once by that error or by an HTTP 400 or 404, and the SDK falls back
    /// to <c>initialize</c>. Without the timeout, only a slow server is treated differently: it is waited for. The accepted
    /// trade-off is a Streamable HTTP server that holds the response open and never answers an unknown method: it now
    /// fails at the caller's connect timeout, 30 seconds by default, instead of falling back after 5.
    /// </para>
    /// <para>
    /// The SDK's fallback can also fail outright. In ModelContextProtocol 2.2.0, when the probe's answer has been read but
    /// not yet handed to the waiting request as the timeout fires, the HTTP transport keeps the probe's protocol version.
    /// It then sends <c>initialize</c> with an <c>MCP-Protocol-Version</c> header that does not match its body, and the
    /// server refuses it with 400. The infinite probe also works around that SDK bug; revisit it once the bug is fixed.
    /// Tracked, with the upstream SDK fix, in Thalos.NET issue #260.
    /// </para>
    /// <para>
    /// The SDK's <c>InitializationTimeout</c>, 60 seconds by default, would otherwise cut a caller's connect timeout that
    /// is longer, so it is lifted too.
    /// </para>
    /// </remarks>
    public static McpClientOptions Create() => new()
    {
        DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
        InitializationTimeout = Timeout.InfiniteTimeSpan,
    };
}
