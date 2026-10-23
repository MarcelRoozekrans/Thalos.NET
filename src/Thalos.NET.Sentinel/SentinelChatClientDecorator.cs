using AI.Sentinel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Thalos.Sentinel;

/// <summary>
/// Registers AI.Sentinel innermost, closest to the provider (Order -1000): it scans exactly what goes to the model — including
/// anything decorators further out inject — and only provider exceptions get wrapped by Sentinel. MAF's function-invocation loop sits
/// outside all decorators, so every model round-trip (tool results included) is scanned. Because Sentinel sits inside function
/// invocation, PII placeholders in the model's tool-call arguments are restored before the tool runs; secret placeholders are
/// never restored into tool arguments, by AI.Sentinel's design.
/// </summary>
public sealed class SentinelChatClientDecorator : IChatClientDecorator
{
    /// <inheritdoc/>
    public int Order => -1000;

    /// <inheritdoc/>
    public IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services)
    {
        var sentinel = new ChatClientBuilder(inner).UseAISentinel().Build(services);
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<SentinelChatClientDecorator>();
        return new SentinelErrorMappingChatClient(sentinel, logger);
    }
}
