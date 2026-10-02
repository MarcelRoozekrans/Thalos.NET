using System.Net;

namespace Thalos.Mcp;

/// <summary>How many bytes one response from a run's endpoint may hold, and whether one has held more.</summary>
internal sealed class ResponseCap
{
    private int _exceeded;

    /// <summary>One response may be at most <paramref name="maxBytes"/> bytes long.</summary>
    public ResponseCap(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxBytes = maxBytes;
    }

    /// <summary>How many bytes one response may hold.</summary>
    public long MaxBytes { get; }

    /// <summary>Whether a response has held more than <see cref="MaxBytes"/> bytes.</summary>
    public bool Exceeded => Volatile.Read(ref _exceeded) != 0;

    /// <summary>Records that a response held more than <see cref="MaxBytes"/> bytes.</summary>
    public void MarkExceeded() => Volatile.Write(ref _exceeded, 1);
}

/// <summary>
/// The HTTP handler of one run's MCP client: it wraps every response's content in a stream that counts the bytes read
/// and faults once more than <see cref="ResponseCap.MaxBytes"/> have been read from one response, and marks the cap
/// exceeded first. The MCP SDK reads a whole answer into memory and has no bound of its own, and the sandbox that
/// answers runs agent-controlled code, so its answers are bounded here.
/// </summary>
internal sealed class ResponseByteCap(ResponseCap cap, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new CappedContent(response.Content, cap);
        return response;
    }

    /// <summary>The original content, read through a <see cref="CountingStream"/>, with its headers.</summary>
    private sealed class CappedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly ResponseCap _cap;

        public CappedContent(HttpContent inner, ResponseCap cap)
        {
            _inner = inner;
            _cap = cap;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new CountingStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _cap);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var counted = new CountingStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _cap);
            await using (counted.ConfigureAwait(false))
            {
                await counted.CopyToAsync(stream).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A read-only stream that faults with <see cref="ResponseTooLargeException"/> past the cap.</summary>
    private sealed class CountingStream(Stream inner, ResponseCap cap) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > cap.MaxBytes)
            {
                cap.MarkExceeded();
                throw new ResponseTooLargeException(cap.MaxBytes);
            }

            return read;
        }
    }
}

/// <summary>A run's endpoint sent a response longer than its <see cref="ResponseCap"/>.</summary>
internal sealed class ResponseTooLargeException(long maxBytes)
    : IOException($"The response is longer than {maxBytes} bytes.");
