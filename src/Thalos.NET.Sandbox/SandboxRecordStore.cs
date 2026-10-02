using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thalos.Git.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>
/// The trusted side's sandbox state under <c>&lt;DataRoot&gt;/sandboxes</c>: one <see cref="SandboxRecord"/> per run,
/// the bundle a create hands its sandbox, the patch a park stores, and each run's lock file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never where a sandbox can write.</b> The directory sits beside, never inside, the publish mirror and worktrees
/// under <c>&lt;DataRoot&gt;/publish</c>, and no container mounts any of it. Records hold bearer tokens, so on Unix the
/// directory is created, or narrowed, to mode 0700 and every file in it to 0600. On Windows the directory inherits
/// <c>DataRoot</c>'s ACL, which the host must keep private.
/// </para>
/// <para>
/// <b>Whole-file writes.</b> A record is written to a temp file, flushed, and moved into place, so a reader sees the old
/// record or the new one and never part of either. A claim is published with <see cref="AtomicPublish.TryPublishNew"/>,
/// which refuses atomically when the run already has a record.
/// </para>
/// </remarks>
/// <param name="dataRoot">The provider's absolute data root.</param>
/// <param name="clock">Ages temp files for the sweep.</param>
/// <param name="logger">Logs cleanup failures.</param>
internal sealed partial class SandboxRecordStore(string dataRoot, TimeProvider clock, ILogger logger)
{
    private const string RecordSuffix = ".json";
    private const string TempSuffix = ".record.tmp";

    /// <summary>A write takes milliseconds, so a temp file this old belongs to a writer that died before its move.</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary><c>&lt;DataRoot&gt;/sandboxes</c>.</summary>
    public string Directory { get; } = Path.Combine(Path.GetFullPath(dataRoot), "sandboxes");

    /// <summary>The run's record.</summary>
    public string RecordPath(Guid runId) => Path.Combine(Directory, runId.ToString("D") + RecordSuffix);

    /// <summary>The bundle a create writes for the run and deletes once imported. Every path here comes from the run id, never from a record's fields.</summary>
    public string BundlePath(Guid runId) => Path.Combine(Directory, runId.ToString("N") + ".bundle");

    /// <summary>Where the run's exported patch is stored.</summary>
    public string PatchPath(Guid runId) => Path.Combine(Directory, runId.ToString("N") + ".patch");

    /// <summary>Where a park streams the run's patch before moving it to <see cref="PatchPath"/>.</summary>
    public string PatchTempPath(Guid runId) => PatchPath(runId) + ".tmp";

    /// <summary>The run's lock: held by a create, a remove, a park or a checkout for its whole length.</summary>
    public string LockPath(Guid runId) => Path.Combine(Directory, "locks", runId.ToString("D") + ".lock");

    /// <summary>Creates the directory owner-only, or narrows an existing one, or says why it cannot be used.</summary>
    public AgentError? EnsureDirectory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.IO.Directory.CreateDirectory(Directory);
            }
            else
            {
                System.IO.Directory.CreateDirectory(Directory, OwnerOnlyDirectory);
                File.SetUnixFileMode(Directory, OwnerOnlyDirectory);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentError.StoreError($"Could not create the sandbox state directory '{Directory}'.", ex.Message);
        }
    }

    /// <summary>
    /// Publishes <paramref name="record"/> only if the run has no record yet. True when it was published, false when the
    /// run already has one.
    /// </summary>
    public async Task<Result<bool, AgentError>> TryClaimAsync(SandboxRecord record, CancellationToken ct)
    {
        var temp = TempPath(record.RunId);
        try
        {
            if (await WriteTempAsync(temp, record, ct).ConfigureAwait(false) is { } failed)
            {
                return Result<bool, AgentError>.Failure(failed);
            }

            return Result<bool, AgentError>.Success(AtomicPublish.TryPublishNew(temp, RecordPath(record.RunId)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<bool, AgentError>.Failure(AgentError.StoreError($"Could not claim the sandbox record of run '{record.RunId}'.", ex.Message));
        }
        finally
        {
            // A no-op after a Windows move; on Unix the link left the temp name behind.
            DeleteFileIfPresent(temp, "remove a sandbox record's temp file");
        }
    }

    /// <summary>Replaces the run's record with <paramref name="record"/> by a whole-file move.</summary>
    public async Task<UnitResult<AgentError>> WriteAsync(SandboxRecord record, CancellationToken ct)
    {
        var temp = TempPath(record.RunId);
        try
        {
            if (await WriteTempAsync(temp, record, ct).ConfigureAwait(false) is { } failed)
            {
                return UnitResult<AgentError>.Failure(failed);
            }

            File.Move(temp, RecordPath(record.RunId), overwrite: true);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not write the sandbox record of run '{record.RunId}'.", ex.Message));
        }
        finally
        {
            DeleteFileIfPresent(temp, "remove a sandbox record's temp file");
        }
    }

    /// <summary>
    /// Reads the run's record: absent, read, or unreadable with the reason. Never throws for the file. A record that is
    /// not well formed for this run, see <see cref="Malformed"/>, is unreadable: it fails closed, like a corrupt file.
    /// </summary>
    public async Task<RecordRead> ReadAsync(Guid runId, CancellationToken ct)
    {
        var read = await ReadFileAsync(RecordPath(runId), ct).ConfigureAwait(false);
        return read.Record is { } record && Malformed(record, runId) is { } reason
            ? new RecordRead(runId, null, reason)
            : read with { RunId = runId };
    }

    /// <summary>
    /// Why <paramref name="record"/> cannot be trusted as the record of <paramref name="runId"/>, or null. The file sits
    /// on the trusted side, but a damaged or hand-edited one must never steer a runtime call, a git read or a token at
    /// another run's sandbox.
    /// </summary>
    internal static string? Malformed(SandboxRecord record, Guid runId)
    {
        if (record.RunId != runId)
        {
            return $"The record names run '{record.RunId}'.";
        }

        if (!string.Equals(record.SandboxId, runId.ToString("N"), StringComparison.Ordinal))
        {
            return "The record's sandbox id is not its run's.";
        }

        if (string.IsNullOrWhiteSpace(record.Token) || string.IsNullOrWhiteSpace(record.Repository) || string.IsNullOrWhiteSpace(record.Remote)
            || string.IsNullOrWhiteSpace(record.DefaultBranch) || string.IsNullOrWhiteSpace(record.Branch))
        {
            return "The record is missing its token, repository, remote or a branch.";
        }

        return GitMirrorStore.IsFullSha(record.BaseCommit ?? "") ? null : "The record's base commit is not a full 40-character sha.";
    }

    /// <summary>
    /// Every record file, readable or not, keyed by the run id its name carries; an unreadable one carries its error, so
    /// a caller can still tell its run is recorded. Also sweeps stale temp files.
    /// </summary>
    public async Task<IReadOnlyList<RecordRead>> ListAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        SweepStaleTemps();
        var records = new List<RecordRead>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + RecordSuffix))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var runId))
            {
                continue;
            }

            records.Add(await ReadAsync(runId, ct).ConfigureAwait(false));
        }

        return records;
    }

    /// <summary>Deletes the run's record; an absent one succeeds.</summary>
    public UnitResult<AgentError> Delete(Guid runId)
    {
        try
        {
            File.Delete(RecordPath(runId));
            return UnitResult<AgentError>.Success();
        }
        catch (DirectoryNotFoundException)
        {
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not delete the sandbox record of run '{runId}'.", ex.Message));
        }
    }

    /// <summary>Deletes <paramref name="path"/> if present; reports whether it is gone, logging why not.</summary>
    public bool DeleteFileIfPresent(string path, string what)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, what, ex.Message);
            return false;
        }
    }

    /// <summary>Options for a new file that only this process's user can read: 0600 on Unix.</summary>
    public static FileStreamOptions NewPrivateFile(FileMode mode = FileMode.CreateNew) => OperatingSystem.IsWindows()
        ? new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous }
        : new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous, UnixCreateMode = OwnerOnlyFile };

    private string TempPath(Guid runId) => Path.Combine(Directory, $".{runId:N}.{Guid.NewGuid():N}{TempSuffix}");

    private static async Task<AgentError?> WriteTempAsync(string temp, SandboxRecord record, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(temp, NewPrivateFile());
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, record, SandboxJsonContext.Default.SandboxRecord, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentError.StoreError($"Could not write the sandbox record '{temp}'.", ex.Message);
        }
    }

    private static async Task<RecordRead> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using (stream.ConfigureAwait(false))
            {
                var record = await JsonSerializer.DeserializeAsync(stream, SandboxJsonContext.Default.SandboxRecord, ct).ConfigureAwait(false);
                return record is null
                    ? new RecordRead(Guid.Empty, null, "The record is empty.")
                    : new RecordRead(Guid.Empty, record, null);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RecordRead(Guid.Empty, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RecordRead(Guid.Empty, null, ex.Message);
        }
    }

    private void SweepStaleTemps()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + TempSuffix))
        {
            if (now - File.GetLastWriteTimeUtc(file) >= StaleTempAge)
            {
                DeleteFileIfPresent(file, "sweep a stale sandbox record temp file");
            }
        }
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning, Message = "Could not {What}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string what, string error);
}

/// <summary>One record file read: absent (both null), read, or unreadable with the reason.</summary>
/// <param name="RunId">The run the file belongs to.</param>
/// <param name="Record">The record, when read.</param>
/// <param name="Error">Why it could not be read, when it could not.</param>
internal readonly record struct RecordRead(Guid RunId, SandboxRecord? Record, string? Error);
