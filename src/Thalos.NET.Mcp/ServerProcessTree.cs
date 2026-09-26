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
/// terminating. Process ids are recycled, so a process counts as the server's only if it was created at or after the
/// start, and a child of the wrapper's id only if it was created before whatever live process holds that id now.
/// Creation times and the start are both read from the system clock the kernel stamps processes with, so they compare.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ServerProcessTree
{
    private const uint Synchronize = 0x0010_0000;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const uint SnapProcess = 0x2;

    /// <summary>The system time, as a <c>FILETIME</c>, from the clock process creation times are read from.</summary>
    public static long Now()
    {
        GetSystemTimeAsFileTime(out var now);
        return now;
    }

    /// <summary>
    /// Kills every process that descends from <paramref name="wrapperPid"/>, and the wrapper itself if it is still
    /// terminating, then waits until each has exited and closed its handles, for at most <paramref name="timeout"/>.
    /// </summary>
    /// <param name="wrapperPid">The process the SDK started, <c>cmd.exe</c>.</param>
    /// <param name="startedAt">From <see cref="Now"/>, taken before the start; a process created before it is not the server's.</param>
    /// <param name="timeout">How long to wait for the killed processes to exit.</param>
    /// <returns>How many processes of the tree were found, and how many of them had not exited when the wait ended.</returns>
    /// <exception cref="Win32Exception">The processes could not be listed.</exception>
    public static async Task<(int Found, int StillRunning)> EndAsync(int wrapperPid, long startedAt, TimeSpan timeout)
    {
        var tree = Collect(wrapperPid, startedAt);
        try
        {
            foreach (var process in tree)
            {
                _ = TerminateProcess(process.SafeWaitHandle, 1); // fails for one already terminating, which the wait covers
            }

            var deadline = DateTime.UtcNow + timeout;
            var stillRunning = 0;
            foreach (var process in tree)
            {
                var left = deadline - DateTime.UtcNow;
                if (!await ExitedAsync(process, left > TimeSpan.Zero ? left : TimeSpan.Zero).ConfigureAwait(false))
                {
                    stillRunning++;
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
    private static List<OpenedProcess> Collect(int wrapperPid, long startedAt)
    {
        var (children, running) = Snapshot();
        var tree = new List<OpenedProcess>();

        // A live process holding the wrapper's id is a later one that reused it, and the wrapper's own children were all
        // created before it. A holder that is terminating and was created after the start is the wrapper itself.
        var childrenBefore = long.MaxValue;
        if (running.Contains(wrapperPid))
        {
            if (OpenedProcess.TryOpen(wrapperPid) is not { } holder)
            {
                childrenBefore = long.MinValue; // cannot tell whose it is: take none of its children
            }
            else if (holder.Created >= startedAt && holder.Terminating)
            {
                tree.Add(holder);
            }
            else
            {
                childrenBefore = holder.Created;
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

                if (child.Created < startedAt || (parent == wrapperPid && child.Created >= childrenBefore))
                {
                    child.Dispose(); // not the server's: its parent id was recycled
                    continue;
                }

                tree.Add(child);
                pending.Enqueue(pid);
            }
        }

        return tree;
    }

    /// <summary>Waits, without blocking a thread, until <paramref name="process"/>'s handle is signalled or <paramref name="timeout"/> passes.</summary>
    private static async Task<bool> ExitedAsync(OpenedProcess process, TimeSpan timeout)
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
    private static (Dictionary<int, List<int>> Children, HashSet<int> Running) Snapshot()
    {
        var children = new Dictionary<int, List<int>>();
        var running = new HashSet<int>();
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

        return (children, running);
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemTimeAsFileTime(out long systemTime);

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
        private OpenedProcess(SafeWaitHandle handle, long created, bool terminating)
        {
            SafeWaitHandle = handle;
            Created = created;
            Terminating = terminating;
        }

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

            return new OpenedProcess(handle, created, exitCode != StillActive);
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

    private sealed class SnapshotHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
