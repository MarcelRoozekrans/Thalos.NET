using System.Net;
using Microsoft.Extensions.AI;
using ZeroAlloc.Rest.Ollama;

namespace Thalos.Ollama;

/// <summary>
/// The M.E.AI <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> over <see cref="OllamaClient"/>: one
/// <c>POST /api/embed</c> per call, one embedding per input in input order, and Ollama's <c>prompt_eval_count</c>
/// as <see cref="UsageDetails.InputTokenCount"/>.
/// </summary>
/// <remarks>
/// A failed request throws <see cref="HttpRequestException"/> carrying Ollama's error text and, when a response
/// described the failure, its status; a transport failure or timeout has no status. A generator built from an
/// endpoint owns its <see cref="HttpClient"/> and disposes it; one built over a borrowed client leaves it alone.
/// Only <see cref="EmbeddingGenerationOptions.ModelId"/> is honoured: <see cref="EmbeddingGenerationOptions.Dimensions"/>
/// and <see cref="EmbeddingGenerationOptions.AdditionalProperties"/> are ignored, because Ollama's <c>/api/embed</c>
/// has no such field.
/// </remarks>
public sealed class OllamaEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly OllamaClient _client;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _defaultModel;
    private EmbeddingGeneratorMetadata? _metadata;

    /// <summary>Creates a generator that owns its HTTP client, pointed at <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">The Ollama base address, for example <c>http://localhost:11434/</c>.</param>
    /// <param name="model">The embedding model when <see cref="EmbeddingGenerationOptions.ModelId"/> is not set.</param>
    public OllamaEmbeddingGenerator(Uri endpoint, string model)
        : this(CreateHttpClient(endpoint), model, ownsHttp: true)
    {
    }

    internal OllamaEmbeddingGenerator(HttpClient http, string model)
        : this(http, model, ownsHttp: false)
    {
    }

    private OllamaEmbeddingGenerator(HttpClient http, string model, bool ownsHttp)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _http = http;
        _ownsHttp = ownsHttp;
        _defaultModel = model;
        _client = OllamaClient.Create(http);
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var inputs = values.ToArray();
        var request = new EmbedRequest(options?.ModelId ?? _defaultModel, inputs);

        var result = await _client.EmbedAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var error = result.Error;
            throw new HttpRequestException(error.Message, inner: null, error.Status == 0 ? null : (HttpStatusCode)error.Status);
        }

        var response = result.Value;
        if (response.Embeddings.Count != inputs.Length)
        {
            throw new InvalidOperationException(
                $"Ollama returned {response.Embeddings.Count} embeddings for {inputs.Length} inputs.");
        }

        var embeddings = new GeneratedEmbeddings<Embedding<float>>(inputs.Length);
        foreach (var vector in response.Embeddings)
        {
            embeddings.Add(new Embedding<float>(vector) { ModelId = response.Model });
        }

        if (response.PromptEvalCount is { } promptTokens)
        {
            embeddings.Usage = new UsageDetails { InputTokenCount = promptTokens, TotalTokenCount = promptTokens };
        }

        return embeddings;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata ??= new EmbeddingGeneratorMetadata("ollama", _http.BaseAddress, _defaultModel);
        }

        return serviceType.IsInstanceOfType(_client) ? _client
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static HttpClient CreateHttpClient(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new HttpClient { BaseAddress = endpoint };
    }
}
