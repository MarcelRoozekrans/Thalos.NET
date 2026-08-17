namespace Thalos;

/// <summary>
/// Outcome of scanning untrusted text before it is injected into a prompt. <see langword="default"/> is a denial — fail closed.
/// <paramref name="Detail"/> is a short diagnostic (e.g. <c>"High: SEC-01"</c>), never the scanned text.
/// </summary>
public readonly record struct UntrustedContentVerdict(bool Allowed, string? Detail)
{
    /// <summary>The content may be injected.</summary>
    public static UntrustedContentVerdict Allow() => new(true, null);

    /// <summary>The content must be dropped; <paramref name="detail"/> explains why (severity + detector id).</summary>
    public static UntrustedContentVerdict Quarantine(string detail) => new(false, detail);
}

/// <summary>
/// Scans text that came from an untrusted source (recalled memories written by earlier model output or tools, retrieved
/// documents) before it is injected into a prompt. Thalos.NET.Sentinel provides an implementation over AI.Sentinel's
/// detection pipeline; when none is registered, consumers inject unscanned but delimited content.
/// </summary>
public interface IUntrustedContentScanner
{
    /// <summary>Returns the verdict for <paramref name="content"/>. Implementations should never throw for ordinary input; a thrown exception is treated as a denial by callers.</summary>
    ValueTask<UntrustedContentVerdict> ScanAsync(string content, CancellationToken ct);
}
