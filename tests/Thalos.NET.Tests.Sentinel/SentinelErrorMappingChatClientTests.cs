using AI.Sentinel.Detection;
using AI.Sentinel.Domain;
using AI.Sentinel.Intervention;
using Microsoft.Extensions.AI;
using Thalos.Sentinel;
using Thalos.Testing;

namespace Thalos.Tests.Sentinel;

/// <summary>Exception mapping of the decorator's inner wrapper, isolated from the AI.Sentinel pipeline (a scripted client throws what Sentinel would).</summary>
public sealed class SentinelErrorMappingChatClientTests
{
    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "hi")];

    private static SentinelException Quarantine(Severity severity, string detectorId, string reason) =>
        new("AI.Sentinel quarantined message.", new PipelineResult(new ThreatRiskScore(70), [new DetectionResult(new DetectorId(detectorId), severity, reason)]));

    private static SentinelErrorMappingChatClient Throwing(Exception exception) =>
        new(new ScriptedChatClient().ThenThrow(exception));

    private static async Task<Exception> StreamAsync(SentinelErrorMappingChatClient client)
    {
        try
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Prompt))
            {
            }
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected the stream to throw");
    }

    [Fact]
    public async Task Buffered_quarantine_maps_to_Quarantined_with_severity_and_detector_only()
    {
        var client = Throwing(Quarantine(Severity.High, "SEC-02", "api_key=SECRET-VALUE"));

        var act = () => client.GetResponseAsync(Prompt);

        var ex = (await act.Should().ThrowAsync<AgentTurnException>()).Which;
        ex.Error.Code.Should().Be(AgentErrorCode.Quarantined);
        ex.Error.Detail.Should().Be("High: SEC-02");
        ex.Error.ToString().Should().NotContain("SECRET", "detector reason text may echo sensitive input");
        ex.InnerException.Should().BeOfType<SentinelException>();
    }

    [Fact]
    public async Task Streaming_quarantine_maps_to_Quarantined_with_severity_and_detector_only()
    {
        var client = Throwing(Quarantine(Severity.Critical, "SEC-01", "Semantic match — high-severity threat pattern"));

        var ex = await StreamAsync(client);

        var ate = ex.Should().BeOfType<AgentTurnException>().Which;
        ate.Error.Code.Should().Be(AgentErrorCode.Quarantined);
        ate.Error.Detail.Should().Be("Critical: SEC-01");
        ate.Error.ToString().Should().NotContain("Semantic match");
    }

    [Fact]
    public async Task Sentinel_exception_without_pipeline_result_maps_to_ProviderError()
    {
        var client = Throwing(new SentinelException("AI.Sentinel rate limit exceeded for session 'x'."));

        var act = () => client.GetResponseAsync(Prompt);

        var ex = (await act.Should().ThrowAsync<AgentTurnException>()).Which;
        ex.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        ex.Error.Detail.Should().Contain("rate limit");
    }

    [Fact]
    public async Task Sentinel_inner_client_wrapper_is_unwrapped_buffered()
    {
        var client = Throwing(new InvalidOperationException("Inner client failed.", new HttpRequestException("503")));

        var act = () => client.GetResponseAsync(Prompt);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("503");
    }

    [Fact]
    public async Task Sentinel_inner_client_wrapper_is_unwrapped_streaming()
    {
        var client = Throwing(new InvalidOperationException("Inner client streaming failed.", new HttpRequestException("503")));

        var ex = await StreamAsync(client);

        ex.Should().BeOfType<HttpRequestException>().Which.Message.Should().Be("503");
    }

    [Fact]
    public async Task Unrelated_exceptions_pass_through()
    {
        var client = Throwing(new TimeoutException("slow"));

        var act = () => client.GetResponseAsync(Prompt);

        (await act.Should().ThrowAsync<TimeoutException>()).WithMessage("slow");
    }
}
