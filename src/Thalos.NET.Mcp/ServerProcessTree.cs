using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Thalos.Mcp;

/// <summary>
/// Ends what is left of a stdio server's process tree once the MCP SDK has let go of it. On Windows the SDK starts a
/// stdio server as <c>cmd.exe /c</c>, and its stop and its crash handling wait only for that wrapper: a stop kills the
/// tree but waits for <c>cmd.exe</c> alone, so the server and its <c>conhost.exe</c> may still be terminating when it
/// returns, and a wrapper that exited first leaves the server running. Either way they still hold the server's
/// working directory: a terminating process reports its exit code before Windows has closed its handles, and only
/// its process handle turning signalled says they are closed.
/// </summary>
/// <remarks>
/// Only Windows needs this: elsewhere the SDK starts the server itself, with no wrapper, and waits for it. The tree is
/// found by parent process id, which Windows keeps after the parent exits, and each process is opened by id, not
/// through <see cref="System.Diagnostics.Process"/>, which refuses a process that has an exit code but is still
/// terminating.
/// <para>
/// Process ids are recycled, and a process listed in the snapshot may exit and give its id to a new one before it is
/// opened. So a process is taken as the server's only if it was created at or after the start and no later than the
/// snapshot, and a child of the wrapper's id only if it was also created before the session closed, a time stamped as it
/// closed, and before any live process that holds that id now. Creation times, the start, the snapshot and the close are all read
/// from the system clock the kernel stamps processes with, so they compare. What these bounds cannot rule out is an id
/// recycled, within that window, into a process whose recorded parent is itself a member of the tree; a job object
/// holding the whole tree would, and is tracked as Thalos.NET issue 192.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ServerProcessTree
{
    private const uint Synchronize = 0x0010_0000;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const uint SnapProcess = 0x2;
    private const int ErrorNoMoreFiles = 18;

    /// <summary>
    /// How long to wait, after terminating them, for the server's processes to finish exiting and closing their handles.
    /// Termination is asynchronous: a killed process holds its handles, the working directory among them, until the
    /// kernel has torn it down, which usually takes milliseconds but can lag by seconds under load. The registry's
    /// contract is that no process holds the workspace as its working directory when a stop returns, so this waits for
    /// the teardown itself. It is not <see cref="McpServerDefinition.ShutdownTimeout"/>, the grace period a server gets to
    /// exit on its own, which the SDK has already spent before it kills; it only keeps a process that never finishes
    /// exiting from holding a stop forever.
    /// </summary>
    public static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The system time, as a <c>FILETIME</c>, at full precision. Process creation times are stamped at full precision;
    /// the coarse system time lags them by up to a clock tick, so a process created just before a coarse reading could
    /// look created after it.
    /// </summary>
    public static long Now()
    {
        GetSystemTimePreciseAsFileTime(out var now);
        return now;
    }

    /// <summary>
    /// Kills every process that descends from <paramref name="wrapperPid"/>, and the wrapper itself if it is still
    /// terminating, then waits until each has exited and closed its handles, for at most <see cref="TerminationWait"/>.
    /// </summary>
    /// <param name="wrapperPid">The process the SDK started, <c>cmd.exe</c>.</param>
    /// <param name="startedAt">From <see cref="Now"/>, taken before the start; a process created before it is not the server's.</param>
    /// <param name="closedAt">From <see cref="Now"/>, stamped as the session closed; the wrapper's own children were all created before it.</param>
    /// <returns>How many processes of the tree were found, and the ids of those that had not exited when the wait ended.</returns>
    /// <exception cref="Win32Exception">The processes could not be listed.</exception>
    public static Task<(int Found, IReadOnlyList<int> StillRunning)> EndAsync(int wrapperPid, long startedAt, long closedAt) =>
        EndAsync(wrapperPid, startedAt, closedAt, Snapshot, WaitForExitAsync);

    /// <summary>
    /// <see cref="EndAsync(int, long, long)"/> over the process list <paramref name="snapshot"/> takes, waiting for each
    /// process's exit signal through <paramref name="waitForExit"/>; a seam for tests.
    /// </summary>
    internal static async Task<(int Found, IReadOnlyList<int> StillRunning)> EndAsync(
        int wrapperPid, long startedAt, long closedAt, Func<ProcessSnapshot> snapshot, Func<WaitHandle, TimeSpan, Task<bool>> waitForExit)
    {
        var tree = Collect(wrapperPid, startedAt, closedAt, snapshot());
        try
        {
            foreach (var process in tree)
            {
                _ = TerminateProcess(process.SafeWaitHandle, 1); // fails for one already terminating, which the wait covers
            }

            var deadline = DateTime.UtcNow + TerminationWait;
            var stillRunning = new List<int>();
            foreach (var process in tree)
            {
                var left = deadline - DateTime.UtcNow;
                if (!await waitForExit(process, left > TimeSpan.Zero ? left : TimeSpan.Zero).ConfigureAwait(false))
                {
                    stillRunning.Add(process.Pid);
                }
            }

            return (tree.Count, stillRunning);
        }
        finally
        {
            foreach (var process in tree)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The server's processes under <paramref name="wrapperPid"/>, each opened, which keeps its id from being reused while it is ended.</summary>
    private static List<OpenedProcess> Collect(int wrapperPid, long startedAt, long closedAt, ProcessSnapshot snapshot)
    {
        var (children, running, snapshotAt) = snapshot;
        var tree = new List<OpenedProcess>();

        // The wrapper's own children were created before its session closed. A live process holding the
        // wrapper's id, created at or after the start and no later than the snapshot, and already terminating, is the
        // wrapper itself; any other holder reused the id, and the wrapper's children were all created before it. A holder
        // created after the snapshot says nothing about who held the id when the snapshot was taken, so none of that id's
        // children is taken.
        var childrenBefore = closedAt;
        if (running.Contains(wrapperPid))
        {
            var holder = OpenedProcess.TryOpen(wrapperPid);
            if (holder is null || holder.Created > snapshotAt)
            {
                holder?.Dispose();
                childrenBefore = long.MinValue;
            }
            else if (holder.Created >= startedAt && holder.Terminating)
            {
                tree.Add(holder);
            }
            else
            {
                childrenBefore = Math.Min(childrenBefore, holder.Created);
                holder.Dispose();
            }
        }

        var seen = new HashSet<int> { wrapperPid };
        var pending = new Queue<int>([wrapperPid]);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var pid in children.GetValueOrDefault(parent) ?? [])
            {
                if (!seen.Add(pid) || OpenedProcess.TryOpen(pid) is not { } child)
                {
                    continue;
                }

                if (child.Created < startedAt || child.Created > snapshotAt || (parent == wrapperPid && child.Created >= childrenBefore))
                {
                    child.Dispose(); // not the server's: its id, or its parent's, was recycled
                    continue;
                }

                tree.Add(child);
                pending.Enqueue(pid);
            }
        }

        return tree;
    }

    /// <summary>
    /// Waits, without blocking a thread, until <paramref name="process"/> is signalled, which for a process handle means
    /// it has exited and closed its handles, or until <paramref name="timeout"/> passes. Whether it was signalled.
    /// </summary>
    internal static async Task<bool> WaitForExitAsync(WaitHandle process, TimeSpan timeout)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            process, static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut), exited, timeout, executeOnlyOnce: true);
        try
        {
            return await exited.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>Every running process's children by parent id, and every running process id, from one Toolhelp snapshot.</summary>
    /// <exception cref="Win32Exception">The snapshot could not be taken or read to its end.</exception>
    internal static ProcessSnapshot Snapshot()
    {
        var children = new Dictionary<int, List<int>>();
        var running = new HashSet<int>();
        // Taken first, deliberately: a process created between this and the snapshot is listed but refused as created
        // after it. That errs towards leaving a process running, never towards ending one that is not the server's; what
        // it can miss is a descendant created in that gap of microseconds.
        var takenAt = Now();
        using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var entry = new ProcessEntry { Size = (uint)Unsafe.SizeOf<ProcessEntry>() };
        for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
        {
            var pid = (int)entry.ProcessId;
            var parent = (int)entry.ParentProcessId;
            running.Add(pid);
            if (!children.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                children[parent] = siblings;
            }

            siblings.Add(pid);
        }

        // The loop ends on any failure, not only at the end of the list: a listing cut short must not pass for a tree
        // with no processes left in it.
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNoMoreFiles)
        {
            throw new Win32Exception(error);
        }

        return new ProcessSnapshot(children, running, takenAt);
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemTimePreciseAsFileTime(out long systemTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(SnapshotHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(SnapshotHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeWaitHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>
    /// An open process handle, as a <see cref="WaitHandle"/> that is signalled once the process has exited and closed
    /// its handles, with the process's creation time.
    /// </summary>
    private sealed class OpenedProcess : WaitHandle
    {
        private OpenedProcess(SafeWaitHandle handle, int pid, long created, bool terminating)
        {
            SafeWaitHandle = handle;
            Pid = pid;
            Created = created;
            Terminating = terminating;
        }

        /// <summary>The process id.</summary>
        public int Pid { get; }

        /// <summary>The creation time, as a <c>FILETIME</c>.</summary>
        public long Created { get; }

        /// <summary>Whether the process already has an exit code, though it may not have closed its handles yet.</summary>
        public bool Terminating { get; }

        /// <summary><paramref name="pid"/>, opened, or <see langword="null"/> when it is gone or cannot be opened.</summary>
        public static OpenedProcess? TryOpen(int pid)
        {
            var handle = OpenProcess(Synchronize | ProcessTerminate | ProcessQueryLimitedInformation, inheritHandle: false, (uint)pid);
            if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _) || !GetExitCodeProcess(handle, out var exitCode))
            {
                handle.Dispose();
                return null;
            }

            return new OpenedProcess(handle, pid, created, exitCode != StillActive);
        }
    }

    /// <summary><c>PROCESSENTRY32W</c>; the executable name at its end is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public ExeFileName ExeFile;
    }

    /// <summary><c>WCHAR szExeFile[MAX_PATH]</c>.</summary>
    [InlineArray(260)]
    private struct ExeFileName
    {
        public ushort First;
    }

    /// <summary>
    /// A process listing: each parent id's children, every listed id, and the time, from <see cref="Now"/>, taken just
    /// before the listing. A process created in between is listed but refused as created after that time, which errs
    /// towards leaving it running; only a descendant created in that gap can be missed.
    /// </summary>
    internal sealed record ProcessSnapshot(Dictionary<int, List<int>> Children, HashSet<int> Running, long TakenAt);

    private sealed class SnapshotHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
