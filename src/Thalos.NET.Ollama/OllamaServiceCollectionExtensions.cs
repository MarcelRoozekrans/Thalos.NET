using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Thalos.Ollama;

/// <summary>Registers the Ollama embedding generator.</summary>
public static class OllamaServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="OllamaEmbeddingGenerator"/> as the singleton
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> and returns that same instance, for a host that needs it at
    /// registration time. The container does not dispose an instance it was handed, so the generator lives as long as the process.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The Ollama base address, for example <c>http://localhost:11434/</c>.</param>
    /// <param name="model">The embedding model.</param>
    /// <returns>The registered generator.</returns>
    public static IEmbeddingGenerator<string, Embedding<float>> UseOllamaEmbeddings(this IServiceCollection services, Uri endpoint, string model)
    {
        ArgumentNullException.ThrowIfNull(services);
        var generator = new OllamaEmbeddingGenerator(endpoint, model);
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingGenerator<string, Embedding<float>>>(generator));
        return generator;
    }
}
