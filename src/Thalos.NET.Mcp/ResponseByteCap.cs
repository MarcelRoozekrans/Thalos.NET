using System.Net;

namespace Thalos.Mcp;

/// <summary>
/// Whether a response one call sent for exceeded the cap. <see cref="ResponseByteCap.Watch"/> makes one for the
/// calling flow, and only the responses to requests sent from that flow mark it, so a concurrent call on the same
/// client never sees another call's overflow as its own.
/// </summary>
internal sealed class ResponseCapHit
{
    private int _hit;

    /// <summary>Whether a response to this call's requests held more than the cap.</summary>
    public bool Hit => Volatile.Read(ref _hit) != 0;

    /// <summary>Records that a response to this call's requests held more than the cap.</summary>
    public void Mark() => Volatile.Write(ref _hit, 1);
}

/// <summary>
/// The HTTP handler of one run's MCP client: it wraps every response's content in a stream that counts the bytes read
/// and faults once more than <paramref name="maxBytes"/> have been read from one response, after marking the
/// <see cref="ResponseCapHit"/> of the call that sent the request, if it watches. The MCP SDK reads a whole answer into
/// memory and has no bound of its own, and the sandbox that answers runs agent-controlled code, so its answers are
/// bounded here.
/// </summary>
internal sealed class ResponseByteCap(long maxBytes, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly AsyncLocal<ResponseCapHit?> Current = new();

    /// <summary>
    /// Starts watching the calling flow: the responses to every request sent from it, until the calling async method
    /// returns, mark the returned hit when they exceed the cap.
    /// </summary>
    public static ResponseCapHit Watch()
    {
        var hit = new ResponseCapHit();
        Current.Value = hit;
        return hit;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var hit = Current.Value;
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new CappedContent(response.Content, maxBytes, hit);
        return response;
    }

    /// <summary>
    /// The original content, read through a <see cref="CountingStream"/>, with its headers. Every read and copy overload
    /// is overridden, the cancellable ones passing their token on, so a slow answer stays cancellable by the call's
    /// timeout however the SDK reads it.
    /// </summary>
    private sealed class CappedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _maxBytes;
        private readonly ResponseCapHit? _hit;

        public CappedContent(HttpContent inner, long maxBytes, ResponseCapHit? hit)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _hit = hit;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Counted(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            Counted(_inner.ReadAsStream(cancellationToken));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var counted = Counted(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            await using (counted.ConfigureAwait(false))
            {
                await counted.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var counted = Counted(_inner.ReadAsStream(cancellationToken));
            counted.CopyTo(stream);
        }

        private CountingStream Counted(Stream inner) => new(inner, _maxBytes, _hit);

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
    private sealed class CountingStream(Stream inner, long maxBytes, ResponseCapHit? hit) : Stream
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
            if (_read > maxBytes)
            {
                hit?.Mark();
                throw new ResponseTooLargeException(maxBytes);
            }

            return read;
        }
    }
}

/// <summary>A run's endpoint sent a response longer than the cap of its <see cref="ResponseByteCap"/>.</summary>
internal sealed class ResponseTooLargeException(long maxBytes)
    : IOException($"The response is longer than {maxBytes} bytes.");
