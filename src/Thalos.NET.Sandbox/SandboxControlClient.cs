using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Thalos.Mcp;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>
/// The trusted side's client of a sandbox host's <c>/control</c> routes: import, ready and export. Every request
/// carries the sandbox's bearer token. Registered as a typed <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// The client's own timeout is switched off: an import may take as long as
/// <see cref="SandboxOptions.ImportTimeout"/>, which is longer than <see cref="HttpClient"/>'s 100 second default, so
/// each call is bounded by its caller's token instead, and <see cref="ReadyAsync"/> by <see cref="ReadyTimeout"/> too.
/// A failure is returned, never thrown; only the caller's own cancellation propagates.
/// </remarks>
public sealed class SandboxControlClient
{
    /// <summary>How long one <c>GET /control/ready</c> may take.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The largest ready answer accepted: 64 KiB. The sandbox is untrusted, so nothing it sends is read whole.</summary>
    internal const int MaxReadyBytes = 64 * 1024;

    /// <summary>The most bytes of a refusal's body read: 4 KiB.</summary>
    internal const int MaxRefusalBytes = 4 * 1024;

    /// <summary>The most characters of a response's text a failure carries.</summary>
    private const int MaxDetailChars = 512;

    private readonly HttpClient _http;

    /// <summary>Creates the client and switches off <paramref name="http"/>'s own timeout; see the remarks.</summary>
    /// <param name="http">The client, from <c>AddHttpClient&lt;SandboxControlClient&gt;()</c>.</param>
    public SandboxControlClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Timeout = Timeout.InfiniteTimeSpan;
        _http = http;
    }

    /// <summary>
    /// <c>POST /control/import?branch=&amp;commit=&amp;solution=</c> with <paramref name="bundle"/> as the body. Succeeds on
    /// 202, when the sandbox stored the bundle and began the import; its progress shows in <see cref="ReadyAsync"/>.
    /// </summary>
    /// <param name="sandbox">The sandbox.</param>
    /// <param name="token">Its bearer token.</param>
    /// <param name="bundle">The bundle, from <c>GitMirrorStore.CreateBundleAsync</c>.</param>
    /// <param name="branch">The run's branch, which the sandbox creates.</param>
    /// <param name="commit">The full sha the sandbox checks out.</param>
    /// <param name="solution">The solution, relative to the repository root; the sandbox requires one.</param>
    /// <param name="ct">Bounds the call.</param>
    public async Task<UnitResult<AgentError>> ImportAsync(SandboxHandle sandbox, string token, Stream bundle, string branch, string commit, string? solution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(bundle);
        var query = $"control/import?branch={Uri.EscapeDataString(branch)}&commit={Uri.EscapeDataString(commit)}&solution={Uri.EscapeDataString(solution ?? "")}";
        using var content = new StreamContent(bundle);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = Request(HttpMethod.Post, sandbox, token, query, content);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.Accepted
                ? UnitResult<AgentError>.Success()
                : UnitResult<AgentError>.Failure(await RefusedAsync("import", response, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            return UnitResult<AgentError>.Failure(Unreachable("import", ex));
        }
    }

    /// <summary><c>GET /control/ready</c>: the sandbox's import, restore and Roslyn state.</summary>
    /// <param name="sandbox">The sandbox.</param>
    /// <param name="token">Its bearer token.</param>
    /// <param name="ct">Cancellation token; the call is also bounded by <see cref="ReadyTimeout"/>.</param>
    public async Task<Result<SandboxReadiness, AgentError>> ReadyAsync(SandboxHandle sandbox, string token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ReadyTimeout);
        using var request = Request(HttpMethod.Get, sandbox, token, "control/ready", content: null);
        try
        {
            // Headers only: the body is the untrusted sandbox's, and is read below up to a cap, never buffered whole.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Result<SandboxReadiness, AgentError>.Failure(await RefusedAsync("ready", response, bounded.Token).ConfigureAwait(false));
            }

            var body = await ReadPrefixAsync(response.Content, MaxReadyBytes + 1, bounded.Token).ConfigureAwait(false);
            if (body.Length > MaxReadyBytes)
            {
                return Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError($"The sandbox's ready answer is larger than {MaxReadyBytes} bytes."));
            }

            var readiness = JsonSerializer.Deserialize(body, SandboxJsonContext.Default.SandboxReadiness);
            return readiness is { Restore: not null, Roslyn: not null }
                ? Result<SandboxReadiness, AgentError>.Success(readiness)
                : Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError("The sandbox's ready answer is incomplete."));
        }
        catch (JsonException ex)
        {
            return Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError("The sandbox's ready answer is not valid JSON.", ex.Message));
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            return Result<SandboxReadiness, AgentError>.Failure(Unreachable("ready", ex));
        }
    }

    /// <summary>
    /// <c>POST /control/export</c>, streamed to a new file at <paramref name="path"/>, owner-only on Unix. Stops reading
    /// and fails once more than <paramref name="maxBytes"/> have arrived; a failure deletes the partial file.
    /// </summary>
    /// <param name="sandbox">The sandbox.</param>
    /// <param name="token">Its bearer token.</param>
    /// <param name="path">Where to store the patch; must not exist.</param>
    /// <param name="maxBytes">The largest patch accepted.</param>
    /// <param name="ct">Bounds the call.</param>
    public async Task<UnitResult<AgentError>> ExportToFileAsync(SandboxHandle sandbox, string token, string path, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var request = Request(HttpMethod.Post, sandbox, token, "control/export", content: null);
        var created = false;
        var stored = false;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return UnitResult<AgentError>.Failure(await RefusedAsync("export", response, ct).ConfigureAwait(false));
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return UnitResult<AgentError>.Failure(TooLarge(maxBytes));
            }

            var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(path, SandboxRecordStore.NewPrivateFile());
                created = true;
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes)
                        {
                            return UnitResult<AgentError>.Failure(TooLarge(maxBytes));
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }

                    target.Flush(flushToDisk: true);
                }
            }

            stored = true;
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct) || ex is UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(Unreachable("export", ex));
        }
        finally
        {
            // Only a file this call created: CreateNew refuses an existing one, which is never this call's to delete.
            if (created && !stored)
            {
                TryDelete(path);
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, SandboxHandle sandbox, string token, string relative, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, new Uri(sandbox.BaseAddress, relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// A failure to reach or read the sandbox, as opposed to the caller's own cancellation: an HTTP or I/O failure, or a
    /// timeout of the call's own bound.
    /// </summary>
    private static bool IsTransportFailure(Exception ex, CancellationToken callerToken) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !callerToken.IsCancellationRequested);

    private static async Task<AgentError> RefusedAsync(string route, HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            // Only the first bytes: a refusal's body is the untrusted sandbox's, of any length.
            text = System.Text.Encoding.UTF8.GetString(await ReadPrefixAsync(response.Content, MaxRefusalBytes, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            text = "";
        }

        // The sandbox is untrusted: its text is cut short and stripped of control characters before it reaches a log.
        var detail = LogSanitizer.Clean(text, MaxDetailChars).Trim();
        return AgentError.ProviderError($"The sandbox refused the {route} request with {(int)response.StatusCode}.", detail.Length == 0 ? null : detail);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of <paramref name="content"/> and stops, whatever the body's length; a
    /// caller that passes one more than it accepts can tell an oversized body from one that fits.
    /// </summary>
    internal static async Task<byte[]> ReadPrefixAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[maxBytes];
            var total = 0;
            int read;
            while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total == maxBytes ? buffer : buffer[..total];
        }
    }

    private static AgentError Unreachable(string route, Exception ex) =>
        AgentError.ProviderError($"The sandbox's {route} request failed.", ex.Message);

    private static AgentError TooLarge(long maxBytes) =>
        AgentError.Validation($"The sandbox's patch is larger than {maxBytes} bytes.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind, a partial patch is still never mistaken for a stored one: the caller only records a patch
            // after a success, and the next export to this path refuses the existing file.
        }
    }
}
