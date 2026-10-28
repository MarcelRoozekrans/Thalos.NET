using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Ollama;

namespace Thalos.Tests.Unit.Ollama;

/// <summary>
/// <see cref="OllamaEmbeddingGenerator"/>, the M.E.AI adapter over <c>OllamaClient</c>, over a stub HTTP handler.
/// </summary>
public sealed class OllamaEmbeddingGeneratorTests
{
    private const string Model = "nomic-embed-text";
    private static readonly Uri Endpoint = new("http://ollama.test:11434/");

    private const string TwoVectors = """{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3],[0.4,0.5,0.6]],"prompt_eval_count":8}""";

    [Fact]
    public async Task Each_input_yields_one_embedding_in_order()
    {
        using var generator = Create(new StubHandler(_ => Json(TwoVectors)));

        var result = await generator.GenerateAsync(["a", "b"]);

        // Red: reverse the embeddings when copying them into the result in GenerateAsync.
        result.Should().HaveCount(2);
        result[0].Vector.ToArray().Should().Equal(0.1f, 0.2f, 0.3f);
        result[1].Vector.ToArray().Should().Equal(0.4f, 0.5f, 0.6f);
    }

    [Fact]
    public async Task The_request_names_the_model_and_every_input()
    {
        var handler = new StubHandler(_ => Json(TwoVectors));
        using var generator = Create(handler);

        await generator.GenerateAsync(["first", "second"]);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        // Red: send only the first input in GenerateAsync.
        body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()).Should().Equal("first", "second");
        // Red: send an empty model name instead of the default model.
        body.RootElement.GetProperty("model").GetString().Should().Be(Model);
        // Red: post to /api/embeddings instead of /api/embed.
        handler.Paths.Should().Equal("/api/embed");
    }

    [Fact]
    public async Task Usage_reports_prompt_eval_count()
    {
        using var generator = Create(new StubHandler(_ => Json(TwoVectors)));

        var result = await generator.GenerateAsync(["a", "b"]);

        // Red: leave Usage unset, or map prompt_eval_count to OutputTokenCount.
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokenCount.Should().Be(8);
    }

    [Fact]
    public async Task Usage_is_absent_when_ollama_reports_no_prompt_eval_count()
    {
        using var generator = Create(new StubHandler(_ => Json("""{"model":"m","embeddings":[[1.0]]}""")));

        var result = await generator.GenerateAsync(["a"]);

        // Red: always set Usage, with InputTokenCount 0, when prompt_eval_count is missing.
        result.Usage.Should().BeNull();
    }

    [Fact]
    public async Task Options_ModelId_overrides_the_default_model()
    {
        var handler = new StubHandler(_ => Json(TwoVectors));
        using var generator = Create(handler);

        await generator.GenerateAsync(["a", "b"], new EmbeddingGenerationOptions { ModelId = "other-model" });

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        // Red: ignore options.ModelId and always send the default model.
        body.RootElement.GetProperty("model").GetString().Should().Be("other-model");
    }

    [Fact]
    public void Metadata_names_ollama_the_endpoint_and_the_model()
    {
        using var generator = Create(new StubHandler(_ => Json(TwoVectors)));

        var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata)).Should().BeOfType<EmbeddingGeneratorMetadata>().Subject;

        // Red: name the provider "anthropic" in the metadata.
        metadata.ProviderName.Should().Be("ollama");
        // Red: report a constant URI instead of the HttpClient base address.
        metadata.ProviderUri.Should().Be(Endpoint);
        // Red: report the options model, or null, instead of the default model.
        metadata.DefaultModelId.Should().Be(Model);
    }

    [Fact]
    public void GetService_returns_the_generator_itself_and_nothing_for_a_key_or_an_unrelated_type()
    {
        using var generator = Create(new StubHandler(_ => Json(TwoVectors)));

        // Red: return null for the generator's own type.
        generator.GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>)).Should().BeSameAs(generator);
        // Red: ignore serviceKey.
        generator.GetService(typeof(EmbeddingGeneratorMetadata), "key").Should().BeNull();
        // Red: return the metadata for any type.
        generator.GetService(typeof(string)).Should().BeNull();
    }

    [Fact]
    public async Task An_error_status_throws_HttpRequestException_with_the_status_and_message()
    {
        using var generator = Create(new StubHandler(_ => Json("""{"error":"model \"nope\" not found"}""", HttpStatusCode.NotFound)));

        var act = async () => await generator.GenerateAsync(["a"]);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        // Red: drop the error message and use a generic one.
        thrown.Message.Should().Be("model \"nope\" not found");
        // Red: leave StatusCode null.
        thrown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_transport_failure_throws_HttpRequestException_without_a_status()
    {
        using var generator = Create(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var act = async () => await generator.GenerateAsync(["a"]);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        // Red: cast Status to HttpStatusCode without mapping 0 to null; StatusCode is then 0, not null.
        thrown.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_call_throws_OperationCanceledException()
    {
        using var generator = Create(new StubHandler(_ => throw new HttpRequestException("cancelled")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await generator.GenerateAsync(["a"], cancellationToken: cts.Token);

        // Red: stop passing cancellationToken to EmbedAsync in GenerateAsync; the call then succeeds.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_response_with_the_wrong_number_of_embeddings_throws()
    {
        using var generator = Create(new StubHandler(_ => Json("""{"model":"m","embeddings":[[1.0]]}""")));

        var act = async () => await generator.GenerateAsync(["a", "b"]);

        // Red: remove the count check in GenerateAsync; one embedding is returned for two inputs.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_public_constructor_reports_its_endpoint_and_its_dispose_is_safe_to_repeat()
    {
        var generator = new OllamaEmbeddingGenerator(Endpoint, Model);

        // Red: report a null ProviderUri because the public constructor forgot BaseAddress.
        generator.GetService(typeof(EmbeddingGeneratorMetadata)).Should().BeOfType<EmbeddingGeneratorMetadata>()
            .Which.ProviderUri.Should().Be(Endpoint);
        generator.Dispose();
        // Red: throw on the second dispose.
        generator.Invoking(g => g.Dispose()).Should().NotThrow();
        // Red: treat the public constructor as borrowing, so Dispose leaves its HttpClient open and this call succeeds.
        var act = async () => await generator.GenerateAsync(["a"]);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_borrowed_http_client_is_not_disposed_with_the_generator()
    {
        var handler = new StubHandler(_ => Json(TwoVectors));
        using var http = new HttpClient(handler) { BaseAddress = Endpoint };
        var generator = new OllamaEmbeddingGenerator(http, Model);

        generator.Dispose();

        // Red: dispose the HttpClient unconditionally; the request then throws ObjectDisposedException.
        (await http.GetAsync("/x")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void UseOllamaEmbeddings_registers_the_instance_it_returns()
    {
        var services = new ServiceCollection();

        var returned = services.UseOllamaEmbeddings(Endpoint, Model);

        using var provider = services.BuildServiceProvider();
        // Red: register a second instance, new OllamaEmbeddingGenerator, instead of the returned one.
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().Should().BeSameAs(returned);
    }

    private static OllamaEmbeddingGenerator Create(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = Endpoint }, Model);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return respond(request);
        }
    }
}
