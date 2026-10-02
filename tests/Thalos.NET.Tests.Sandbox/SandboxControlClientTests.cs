using System.Net;
using System.Text;
using AwesomeAssertions.Execution;
using Thalos.Sandbox;

namespace Thalos.Tests.Sandbox;

/// <summary>I3: the untrusted sandbox's answers are read up to a cap, never buffered whole.</summary>
public sealed class SandboxControlClientTests
{
    private static readonly SandboxHandle Handle = new("sandbox", Guid.NewGuid(), SandboxState.Running, new Uri("http://sandbox.invalid/"), DateTimeOffset.UtcNow);

    /// <summary>
    /// A ready answer of 1 MiB, valid JSON, is refused after reading just past the 64 KiB cap. Red: read the body whole
    /// with ResponseContentRead and deserialize it; the call succeeds and reads the whole megabyte.
    /// </summary>
    [Fact]
    public async Task An_oversized_ready_answer_is_refused_without_reading_it_whole()
    {
        var json = $"{{\"imported\":true,\"restore\":\"ok\",\"restoreDetail\":\"{new string('x', 1024 * 1024)}\",\"roslyn\":\"ready\",\"detail\":null}}";
        var body = new CountingStream(Encoding.UTF8.GetBytes(json));
        using var http = new HttpClient(new FixedResponse(HttpStatusCode.OK, body));
        var client = new SandboxControlClient(http);

        var ready = await client.ReadyAsync(Handle, "token", CancellationToken.None);

        using var _ = new AssertionScope();
        ready.IsFailure.Should().BeTrue();
        ready.Error.Message.Should().Contain("larger than");
        body.BytesRead.Should().BeLessThanOrEqualTo(SandboxControlClient.MaxReadyBytes + 1);
    }

    /// <summary>
    /// A refusal with a 1 MiB body is reported from its first 4 KiB only. Red: read the refusal's body with
    /// ReadAsStringAsync; the whole megabyte is read.
    /// </summary>
    [Fact]
    public async Task A_refusal_body_is_read_only_up_to_its_cap()
    {
        var body = new CountingStream(Encoding.UTF8.GetBytes(new string('e', 1024 * 1024)));
        using var http = new HttpClient(new FixedResponse(HttpStatusCode.InternalServerError, body));
        var client = new SandboxControlClient(http);

        var ready = await client.ReadyAsync(Handle, "token", CancellationToken.None);

        using var _ = new AssertionScope();
        ready.IsFailure.Should().BeTrue();
        ready.Error.Message.Should().Contain("500");
        body.BytesRead.Should().BeLessThanOrEqualTo(SandboxControlClient.MaxRefusalBytes);
    }

    /// <summary>A ready answer within the cap is read. Red: set MaxReadyBytes to 16; the small answer is refused.</summary>
    [Fact]
    public async Task A_ready_answer_within_the_cap_is_read()
    {
        var body = new CountingStream(Encoding.UTF8.GetBytes("{\"imported\":true,\"restore\":\"failed\",\"restoreDetail\":\"x\",\"roslyn\":\"ready\",\"detail\":null}"));
        using var http = new HttpClient(new FixedResponse(HttpStatusCode.OK, body));
        var client = new SandboxControlClient(http);

        var ready = await client.ReadyAsync(Handle, "token", CancellationToken.None);

        ready.IsSuccess.Should().BeTrue(ready.IsFailure ? ready.Error.ToString() : "");
        ready.Value.Should().Be(new SandboxReadiness(true, "failed", "x", "ready", null));
    }

    /// <summary>Answers every request with <paramref name="status"/> and <paramref name="body"/>, with no Content-Length.</summary>
    private sealed class FixedResponse(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(body) });
    }

    /// <summary>A readable, non-seekable stream that counts what was read from it.</summary>
    private sealed class CountingStream(byte[] data) : Stream
    {
        private int _position;

        public long BytesRead => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
