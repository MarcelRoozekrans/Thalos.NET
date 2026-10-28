using ZeroAlloc.Rest.Anthropic;

namespace Thalos.Anthropic;

/// <summary>
/// A prompt-cache breakpoint the prompt-cache translator places on a content item or a tool, under
/// <see cref="AnthropicChatClient.CacheBreakpointKey"/> in its additional properties. <see cref="AnthropicChatClient"/>
/// reads only this type there, and only Thalos can construct it, so a caller cannot place a breakpoint of its own.
/// </summary>
/// <param name="Control">The <c>cache_control</c> the marked block or tool is sent with.</param>
internal sealed record CacheBreakpoint(CacheControl Control);
