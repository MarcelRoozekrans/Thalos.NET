using System.Diagnostics;
using System.Runtime.InteropServices;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Tests.Unit.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Workspaces;

public sealed class WorkspaceToolsTests : IDisposable
{
    private static readonly Guid RunId = Guid.NewGuid();

    /// <summary>
    /// The contention timeout for the tests that queue hundreds of calls on one file. They test that the calls take
    /// turns without colliding, not how fast the queue drains, and with every test assembly running at once the last
    /// of 300 serialized writes can wait longer than the 5-second default. Past that bound a call correctly returns
    /// busy, which is not what these tests are about.
    /// </summary>
    private static readonly TimeSpan SameFileQueueTimeout = TimeSpan.FromMinutes(2);
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            DeleteTempDirectory(dir);
        }
    }

    /// <summary>
    /// Deletes a directory one of these tests created. <see cref="Directory.Delete(string, bool)"/>'s own
    /// recursive walk throws <see cref="UnauthorizedAccessException"/> the instant it reaches a directory
    /// junction anywhere under <paramref name="dir"/> — the swapped-in links the security tests above create by
    /// the dozen — whether or not the junction's target still exists, and leaves <paramref name="dir"/> behind
    /// when it does. So every link under <paramref name="dir"/> is removed as the link itself first, which never
    /// fails this way; the ordinary recursive delete that follows then has nothing but plain files and
    /// directories left to remove. This never resolves a link's target, so an "outside" directory these tests
    /// point links at is never reached from here, only ever named directly by its own registered temp path.
    /// </summary>
    private static void DeleteTempDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        RemoveLinksUnder(dir);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Removes every symlink or junction found anywhere under <paramref name="dir"/>, as the link entry itself, without ever descending into what it points at.</summary>
    private static void RemoveLinksUnder(string dir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                RemoveLinksUnder(entry);
            }
        }
    }

    /// <summary>
    /// Round-3 finding A, the Critical: <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> are architecture-dependent on Linux.
    /// The generic ABI, which x86_64 uses, defines them as 0x10000 and 0x20000; arm64 overrides them with 0x4000 and
    /// 0x8000. <see cref="PinnedIo.FlagsFor"/> is the table ruling (a) asks for. A pure function of its argument, so
    /// this pins it without needing to run under either architecture.
    /// </summary>
    [Fact]
    public void The_linux_o_directory_and_o_no_follow_flags_are_pinned_per_architecture()
    {
        PinnedIo.FlagsFor(Architecture.X64).Should().Be((0x10000, 0x20000));
        PinnedIo.FlagsFor(Architecture.Arm64).Should().Be((0x4000, 0x8000));
        PinnedIo.FlagsFor(Architecture.Arm).Should().BeNull();
    }

    /// <summary>
    /// Ruling (a)'s other half: an architecture the table above does not cover must refuse before ever calling into
    /// libc, rather than opening with flag values that might mean something else entirely there. Every tool starts
    /// its chain at <see cref="PinnedDirectory.OpenRoot"/>, so every tool refuses. The architecture comes from the
    /// instance seam <see cref="WorkspaceTools.ArchitectureOverrideForTesting"/> (ruling (r)), so no other test sees
    /// it. Linux-only, since <see cref="PinnedDirectory.OpenRoot"/> reads the table only in its Linux branch.
    /// </summary>
    [SkippableFact]
    public async Task An_unsupported_linux_architecture_refuses_every_call_before_any_open()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the architecture table is read only on Linux");
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "existing.cs"), "original");
        tools.ArchitectureOverrideForTesting = Architecture.Arm;

        using var scope = new AssertionScope();
        (await tools.WriteFile(Caller(RunId), "a.cs", "x")).Should().StartWith("error:");
        File.Exists(Path.Combine(root, "a.cs")).Should().BeFalse();
        (await tools.ReadFile(Caller(RunId), "existing.cs")).Should().StartWith("error:");
        (await tools.EditFile(Caller(RunId), "existing.cs", "original", "edited")).Should().StartWith("error:");
        File.ReadAllText(Path.Combine(root, "existing.cs")).Should().Be("original");
        (await tools.ListFiles(Caller(RunId))).Should().StartWith("error:");
    }

    /// <summary>
    /// Round-3 finding B4: on Windows, opening a directory as though it were the plain file <c>write_file</c>
    /// expects used to throw <see cref="ArgumentOutOfRangeException"/> from <see cref="FileStream.SetLength"/>,
    /// because <c>CreateFileW</c>'s backup semantics happily open a directory. Ruling (f): refused cleanly instead,
    /// on both OSes — on Linux, opening a directory without <c>O_DIRECTORY</c> already fails at the kernel level
    /// with <c>EISDIR</c>, so this exercises the same contract there without needing a separate code path.
    /// </summary>
    [Fact]
    public async Task Opening_a_directory_where_a_file_is_expected_is_refused_without_throwing()
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, "dir.cs"));

        var act = () => tools.WriteFile(Caller(RunId), "dir.cs", "content");

        (await act.Should().NotThrowAsync()).Which.Should().StartWith("error:");
    }

    /// <summary>
    /// Ruling (f) for the other two leaf opens: <c>read_file</c> and <c>edit_file</c> open an existing leaf through
    /// <see cref="PinnedDirectory.OpenExistingFile"/>, whose own directory check refuses a directory named like a
    /// file before its handle reaches a <see cref="FileStream"/>. On Linux a read-write open of a directory fails
    /// with <c>EISDIR</c> in the kernel, but a read-only one succeeds and the first read throws, so the check is
    /// needed there too: this test found that on Linux in round 4.
    /// </summary>
    [Theory]
    [InlineData("read")]
    [InlineData("edit")]
    public async Task Reading_or_editing_a_directory_where_a_file_is_expected_is_refused_without_throwing(string operation)
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, "dir.cs"));

        var act = () => string.Equals(operation, "read", StringComparison.Ordinal)
            ? tools.ReadFile(Caller(RunId), "dir.cs")
            : tools.EditFile(Caller(RunId), "dir.cs", "a", "b");

        (await act.Should().NotThrowAsync()).Which.Should().StartWith("error:");
    }

    /// <summary>
    /// Ruling (j): a leaf open refused only because something else currently holds it — here, an external,
    /// non-participating <see cref="FileStream"/> opened with <see cref="FileShare.None"/> — gets the distinct busy
    /// text once the bounded wait for it to let go runs out, never the one generic refusal text a policy check
    /// returns. Ruling (k): that wait is real, retrying the open with backoff for the whole
    /// <see cref="RunWorkspaceToolOptions.ContentionTimeout"/>, so the call cannot return before it has passed.
    /// Windows-only: <see cref="FileShare.None"/> is a Windows, kernel-enforced concept; a plain Linux <c>open()</c>
    /// has no equivalent mandatory sharing conflict, so a second writer's own open there succeeds regardless of
    /// another handle's requested share mode.
    /// </summary>
    [SkippableFact]
    public async Task Write_file_reports_a_distinct_busy_result_when_another_holder_never_lets_go()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "FileShare.None is a Windows-enforced concept");
        var timeout = TimeSpan.FromMilliseconds(500);
        var (tools, _, root) = Build(contentionTimeout: timeout);
        var target = Path.Combine(root, "held.cs");
        File.WriteAllText(target, "original");

        string result;
        var elapsed = Stopwatch.StartNew();
        using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await tools.WriteFile(Caller(RunId), "held.cs", "hijack");
            elapsed.Stop();
        }

        result.Should().Be("error: the file is busy; try again.");
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(timeout - TimeSpan.FromMilliseconds(50), "the busy result comes only after retrying for the whole contention timeout");
        File.ReadAllText(target).Should().Be("original");
    }

    /// <summary>
    /// Ruling (k), the falsifiable half of the wait: a holder outside the tool's own locks — an ordinary
    /// <see cref="FileStream"/> with <see cref="FileShare.None"/> — lets go while the call is still waiting, and the
    /// call then succeeds instead of reporting busy. The holder is released from the seam on the second attempt to
    /// open the leaf, so the release lands inside the wait by construction, not by timing. Windows-only, for the
    /// same reason as the busy test above.
    /// </summary>
    [SkippableTheory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("edit")]
    public async Task A_holder_that_lets_go_during_the_wait_lets_the_call_succeed(string operation)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "FileShare.None is a Windows-enforced concept");
        var (tools, _, root) = Build(contentionTimeout: TimeSpan.FromSeconds(30));
        var target = Path.Combine(root, "held.cs");
        File.WriteAllText(target, "original");
        var holder = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var leafAttempts = 0;
        tools.BeforeOpenForTesting = candidate =>
        {
            if (string.Equals(Path.GetFileName(candidate), "held.cs", StringComparison.Ordinal) && ++leafAttempts == 2)
            {
                holder.Dispose();
            }
        };

        try
        {
            var result = operation switch
            {
                "write" => await tools.WriteFile(Caller(RunId), "held.cs", "written"),
                "read" => await tools.ReadFile(Caller(RunId), "held.cs"),
                _ => await tools.EditFile(Caller(RunId), "held.cs", "original", "edited"),
            };

            using var scope = new AssertionScope();
            leafAttempts.Should().BeGreaterThanOrEqualTo(2, "the release happens on the second attempt, inside the wait");
            result.Should().Be(operation switch
            {
                "write" => "wrote 7 bytes to 'held.cs'.",
                "read" => "original",
                _ => "edited 'held.cs'.",
            });
        }
        finally
        {
            holder.Dispose();
        }
    }

    /// <summary>
    /// Round-4 finding N1: with 100 truly parallel writes and 100 truly parallel reads of one file, reads opened the
    /// leaf without the per-path lock writes take, so a read met a write's exclusive handle and returned busy at
    /// once, and a write met a read's handle the same way. Ruling (k): reads take the same lock, so every call
    /// succeeds on both OSes, and every read sees one whole version of the file.
    /// </summary>
    [Fact]
    public async Task Concurrent_reads_and_writes_of_the_same_file_all_succeed()
    {
        var (tools, _, root) = Build(contentionTimeout: SameFileQueueTimeout);
        File.WriteAllText(Path.Combine(root, "shared.cs"), "content-initial");
        var caller = Caller(RunId);

        var writes = Enumerable.Range(0, 100).Select(i => Task.Run(() => tools.WriteFile(caller, "shared.cs", $"content-{i:D3}"))).ToList();
        var reads = Enumerable.Range(0, 100).Select(_ => Task.Run(() => tools.ReadFile(caller, "shared.cs"))).ToList();
        var written = await Task.WhenAll(writes);
        var read = await Task.WhenAll(reads);

        written.Should().OnlyContain(r => r.StartsWith("wrote", StringComparison.Ordinal));
        var versions = Enumerable.Range(0, 100).Select(i => $"content-{i:D3}").Append("content-initial").ToHashSet(StringComparer.Ordinal);
        read.Should().OnlyContain(r => versions.Contains(r));
    }

    /// <summary>
    /// Round-4 finding N5 and ruling (l): the per-path lock table held one entry for every path ever written, 1,151
    /// after 1,000 distinct writes. Entries are reference-counted and removed at zero, so after the writes finish the
    /// table is back where it started. The instance gets its own table, so no other test's calls reach the count.
    /// </summary>
    [Fact]
    public async Task The_leaf_lock_table_returns_to_its_baseline_after_a_thousand_distinct_writes()
    {
        var (tools, _, _) = Build();
        var table = new LeafLockTable();
        tools.LeafLocks = table;
        var caller = Caller(RunId);

        for (var i = 0; i < 1000; i++)
        {
            (await tools.WriteFile(caller, $"f{i}.cs", "x")).Should().StartWith("wrote");
        }

        table.Count.Should().Be(0);
    }

    /// <summary>
    /// Ruling (l)'s race: a caller that gives up waiting must drop only its own reference, never the entry the
    /// holder still uses, or the next caller would be handed a fresh semaphore and enter while the holder is still
    /// inside. Once the holder lets go, the entry is gone.
    /// </summary>
    [Fact]
    public async Task A_caller_that_gives_up_waiting_never_evicts_a_lock_still_held()
    {
        var table = new LeafLockTable();

        var holder = await table.AcquireAsync("dir/f.cs", TimeSpan.Zero, CancellationToken.None);
        holder.Should().NotBeNull();
        (await table.AcquireAsync("DIR/F.CS", TimeSpan.Zero, CancellationToken.None)).Should().BeNull("the key compares case-insensitively and the lock is held");
        (await table.AcquireAsync("dir/f.cs", TimeSpan.Zero, CancellationToken.None)).Should().BeNull("the caller that gave up must not have removed the holder's entry");

        holder!.Dispose();
        table.Count.Should().Be(0);
    }

    /// <summary>
    /// Ruling (s): <see cref="RunWorkspaceToolOptions.ContentionTimeout"/> is validated when the tools are
    /// registered, following the <c>Validate(nameof(configure))</c> pattern <c>UseRagNetMemory</c> uses:
    /// <see cref="Timeout.InfiniteTimeSpan"/> and anything from zero to <see cref="int.MaxValue"/> milliseconds are
    /// accepted, and anything else is refused there instead of failing on every call. The two size limits, which
    /// the same method now checks, are refused when negative.
    /// </summary>
    [Theory]
    [InlineData("timeout", -1d, true)]
    [InlineData("timeout", 0d, true)]
    [InlineData("timeout", 2147483647d, true)]
    [InlineData("timeout", -2d, false)]
    [InlineData("timeout", 2147483648d, false)]
    [InlineData("read", -1d, false)]
    [InlineData("list", -1d, false)]
    public void Registration_refuses_options_that_cannot_work(string member, double value, bool valid)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        Action<RunWorkspaceToolOptions> configure = member switch
        {
            "timeout" => o => o.ContentionTimeout = TimeSpan.FromMilliseconds(value),
            "read" => o => o.MaxReadBytes = (int)value,
            _ => o => o.MaxListEntries = (int)value,
        };

        var act = () => new ServiceCollection().AddThalos(t => t.UseRunWorkspaceTools(extensions, configure));

        if (valid)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("configure");
        }
    }

    /// <summary>
    /// A protected path entry with a '..' segment is refused when the tools are registered, not by the first tool call
    /// that builds the set. Red: drop the ProtectedPathSet check from RunWorkspaceToolOptions.Validate.
    /// </summary>
    [Fact]
    public void Registration_refuses_a_protected_path_with_a_parent_segment()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };

        var act = () => new ServiceCollection().AddThalos(t => t.UseRunWorkspaceTools(extensions, o => o.ProtectedPaths.Add("docs/../AGENT.md")));

        act.Should().Throw<ArgumentException>().WithMessage("*ProtectedPaths*'..'*").Which.ParamName.Should().Be("configure");
    }

    /// <summary>
    /// Round-3 finding B2: on Windows, <c>list_files</c>' own directory opens requested <c>DELETE</c> access, so 36
    /// of 50 truly parallel listings of the same directory silently came back missing its contents — a second,
    /// concurrent pin's open collided with the first and <see cref="WorkspaceTools.ListFiles"/> caught the resulting
    /// refusal the same way it treats a genuinely empty directory. Ruling (e): list_files opens read-only, so
    /// concurrent pins of the same directory never collide with each other at all.
    /// </summary>
    [Fact]
    public async Task Concurrent_listings_of_the_same_directory_never_lose_its_contents()
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(root, "sub", $"f{i}.cs"), "x");
        }

        var caller = Caller(RunId);
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() => tools.ListFiles(caller)));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(listing => Enumerable.Range(0, 5).All(i => listing.Contains($"sub/f{i}.cs", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Round-4 finding N4, ruling (h): <c>read_file</c> and <c>edit_file</c> reach the leaf through a pinned chain of
    /// ancestors, each verified when it is opened. Here <c>a</c> is swapped for a link to the in-workspace <c>b</c>
    /// right before the chain opens it, so the leaf's own containment check passes either way: only the ancestor's
    /// verification, and on Linux its <c>O_NOFOLLOW</c> too, stands between the call and <c>b/f.cs</c>.
    /// </summary>
    [SkippableTheory]
    [InlineData("read")]
    [InlineData("edit")]
    public async Task An_ancestor_swapped_for_a_link_inside_the_workspace_is_not_followed(string operation)
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllText(Path.Combine(root, "a", "f.cs"), "old-a");
        File.WriteAllText(Path.Combine(root, "b", "f.cs"), "old-b");
        var swapped = false;
        tools.BeforeOpenForTesting = candidate =>
        {
            if (!swapped && string.Equals(Path.GetFileName(candidate), "a", StringComparison.Ordinal))
            {
                swapped = true;
                Directory.Delete(Path.Combine(root, "a"), recursive: true);
                CreateDirectoryLinkOrSkip(Path.Combine(root, "a"), Path.Combine(root, "b"));
            }
        };

        var result = string.Equals(operation, "read", StringComparison.Ordinal)
            ? await tools.ReadFile(Caller(RunId), "a/f.cs")
            : await tools.EditFile(Caller(RunId), "a/f.cs", "old", "new");

        using var scope = new AssertionScope();
        swapped.Should().BeTrue();
        result.Should().StartWith("error:");
        if (string.Equals(operation, "edit", StringComparison.Ordinal))
        {
            File.ReadAllText(Path.Combine(root, "b", "f.cs")).Should().Be("old-b");
        }
    }

    /// <summary>
    /// Round-4 finding N4, ruling (e): an enumeration that fails must come back as a failure, never as a listing
    /// cut short. On Linux a directory removed while its descriptor is still open makes <c>getdents64</c> fail with
    /// <c>ENOENT</c>, which is a real enumeration error rather than the end of the listing. Linux-only: on Windows the
    /// pin itself stops the directory from being removed.
    /// </summary>
    [SkippableFact]
    public void An_enumeration_error_is_a_failure_not_a_short_listing()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "a pinned directory can be removed only on Linux");
        var dir = NewTempDir("thalos-workspace-tools-enumerate-");
        var pinned = PinnedDirectory.OpenRoot(dir, new DirectoryLevelTable());
        pinned.IsSuccess.Should().BeTrue();

        using var directory = pinned.Value;
        Directory.Delete(dir);

        directory.EnumerateEntries().IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Write_then_read_round_trips_inside_the_run_workspace_and_notifies_listeners()
    {
        var (tools, listener, root) = Build();

        (await tools.WriteFile(Caller(RunId), "src/New.cs", "class N {}")).Should().StartWith("wrote");
        File.ReadAllText(Path.Combine(root, "src", "New.cs")).Should().Be("class N {}");
        (await tools.ReadFile(Caller(RunId), "src/New.cs")).Should().Be("class N {}");
        listener.Changes.Should().ContainSingle().Which.Should().Be((RunId, "src/New.cs"));
    }

    [Fact]
    public async Task A_caller_without_a_run_claim_touches_nothing() =>
        (await Build().Tools.WriteFile(new TestSecurityContext("chat-user"), "x.txt", "y")).Should().StartWith("error: this turn has no run workspace");

    /// <summary>
    /// Against targets that already exist with an allow-listed extension, so an unconfined write would actually
    /// succeed rather than merely fail with "does not exist" — a plain refusal on a target that isn't there can't
    /// tell a genuine confinement refusal from one that never had anything to reach.
    /// </summary>
    [Fact]
    public async Task Escapes_are_refused_even_when_the_outside_and_git_targets_already_exist()
    {
        var (tools, _, root) = Build();
        var outsideFile = Path.Combine(Path.GetDirectoryName(root)!, $"escape-{RunId:N}.cs");
        File.WriteAllText(outsideFile, "untouched");
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        try
        {
            (await tools.WriteFile(Caller(RunId), "../" + Path.GetFileName(outsideFile), "hijack")).Should().StartWith("error:");
            (await tools.WriteFile(Caller(RunId), ".git/x.cs", "hijack")).Should().StartWith("error:");

            File.ReadAllText(outsideFile).Should().Be("untouched");
            File.Exists(Path.Combine(root, ".git", "x.cs")).Should().BeFalse();
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task A_protected_path_is_readable_but_not_writable()
    {
        var (tools, _, root) = Build(protectedPaths: ["AGENT.md"]);
        File.WriteAllText(Path.Combine(root, "AGENT.md"), "pinned");

        (await tools.ReadFile(Caller(RunId), "AGENT.md")).Should().Be("pinned");
        (await tools.WriteFile(Caller(RunId), "agent.MD", "hijack")).Should().Contain("protected");
        (await tools.EditFile(Caller(RunId), "AGENT.md", "pinned", "x")).Should().Contain("protected");
        File.ReadAllText(Path.Combine(root, "AGENT.md")).Should().Be("pinned");
    }

    [SkippableFact]
    public async Task A_link_to_a_protected_path_is_also_protected()
    {
        var (tools, _, root) = Build(protectedPaths: ["AGENT.md"]);
        File.WriteAllText(Path.Combine(root, "AGENT.md"), "pinned");
        CreateFileSymlinkOrSkip(Path.Combine(root, "alias.md"), Path.Combine(root, "AGENT.md"));

        (await tools.WriteFile(Caller(RunId), "alias.md", "hijack")).Should().Contain("protected");
        File.ReadAllText(Path.Combine(root, "AGENT.md")).Should().Be("pinned");
    }

    /// <summary>
    /// The swap the review found: <c>sub</c> is a real directory containing no <c>AGENT.md</c> of its own, so
    /// <c>WorkspacePath.Resolve("sub/AGENT.md")</c> resolves cleanly and the pre-check — comparing
    /// <c>"sub/AGENT.md"</c> against the protected path <c>"AGENT.md"</c> — correctly sees no match. Between that
    /// resolve and the tool's own open, <c>sub</c> is swapped for a junction pointing at the workspace root itself,
    /// via the <see cref="WorkspaceTools.BeforeOpenForTesting"/> seam. This round's chain pinning now catches the
    /// swap even earlier than the leaf-level protected-path check: pinning <c>sub</c> as a chain member verifies
    /// its real path equals the pinned root's own real path plus <c>"sub"</c> exactly, which the swapped junction's
    /// real path — the root itself — does not, so the whole chain build refuses with the same generic message
    /// <see cref="WorkspacePath.Resolve"/> itself uses, before the write ever reaches the leaf or the protected-path
    /// check. The security property under test is unchanged either way: <c>AGENT.md</c> is never touched.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_through_a_subdirectory_does_not_bypass_agent_md_protection()
    {
        var (tools, _, root) = Build(protectedPaths: ["AGENT.md"]);
        File.WriteAllText(Path.Combine(root, "AGENT.md"), "pinned");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var cleanupFired = false;
        tools.BeforeCleanupForTesting = () => cleanupFired = true;

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"));
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), root);
        };

        (await tools.WriteFile(Caller(RunId), "sub/AGENT.md", "hijack")).Should().StartWith("error:");
        File.ReadAllText(Path.Combine(root, "AGENT.md")).Should().Be("pinned");
        cleanupFired.Should().BeTrue("even a chain-build refusal must run the cleanup seam, so a test can prove that window is safe too");
    }

    /// <summary>
    /// The review's second finding, and the "P02" variant it asked for: <c>sub</c> is a real, empty directory;
    /// before this call's own chain ever pins it, <c>sub</c> is swapped for a link to a directory outside the
    /// workspace — via <see cref="WorkspaceTools.BeforeOpenForTesting"/>, fired from inside
    /// <see cref="PinnedDirectory.CreateChild"/> itself, since <c>sub</c> is not yet a held-open, unswappable handle
    /// at that point. The chain build's own per-level verification — <c>sub</c>'s real path must equal the pinned
    /// root's own real path plus <c>"sub"</c> exactly — catches this before <c>write_file</c> ever reaches
    /// <c>FileMode.CreateNew</c> or the leaf. <see cref="WorkspaceTools.BeforeCleanupForTesting"/> still fires from
    /// the chain-build failure branch, proving that window is safe too, even though nothing was created to clean up.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_through_a_subdirectory_leaves_nothing_outside()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var cleanupFired = false;
        tools.BeforeCleanupForTesting = () => cleanupFired = true;

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"));
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
        };

        var result = await tools.WriteFile(Caller(RunId), "sub/new.cs", "hijack");

        result.Should().Contain("not permitted");
        File.Exists(Path.Combine(outside, "new.cs")).Should().BeFalse();
        cleanupFired.Should().BeTrue();
    }

    /// <summary>
    /// "P03" isolated from any ancestor swap the chain build would already catch on its own: <c>sub</c> stays real
    /// and legitimately pinned all the way through the leaf's own creation — the refusal comes from narrowing the
    /// allow-list, via <see cref="WorkspaceTools.BeforeOpenForTesting"/>, to nothing right after the pre-check
    /// already passed it. <see cref="WorkspaceTools.BeforeCleanupForTesting"/> then runs once the refused file is
    /// removed and its handle closed, the window in which the review's bug — deleting by <c>confined.RealPath</c>,
    /// re-resolved after the handle was disposed — would follow a swapped <c>sub</c> to <c>outside</c>'s own, unrelated
    /// <c>new.cs</c>. On Linux the swap goes through and the outside file must survive it. On Windows, ruling (m):
    /// with the leaf's handle already closed, only the pin on <c>sub</c> can stop the swap, so the swap must fail with
    /// a sharing violation, HResult low word 32.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_at_the_cleanup_seam_cannot_redirect_file_removal()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        var (tools, _, root) = Build(allowedWriteExtensions: allowed);
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        File.WriteAllText(Path.Combine(outside, "new.cs"), "outside-original");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("new.cs", StringComparison.Ordinal))
            {
                allowed.Clear(); // the post-check, run after this open, now refuses every extension
            }
        };
        tools.BeforeCleanupForTesting = () =>
        {
            var swap = () =>
            {
                Directory.Delete(Path.Combine(root, "sub"), recursive: true);
                CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
            };

            if (OperatingSystem.IsWindows())
            {
                // Rulings (g) and (m): sub is still pinned with FILE_SHARE_DELETE excluded and the leaf's handle is
                // closed, so the sharing violation can come only from that pin.
                SwapIsRefusedWithASharingViolation(swap);
                return;
            }

            swap();
        };

        var result = await tools.WriteFile(Caller(RunId), "sub/new.cs", "hijack");

        result.Should().Contain("extension");
        var outsideFile = Path.Combine(outside, "new.cs");
        (File.Exists(outsideFile) ? File.ReadAllText(outsideFile) : "(deleted)").Should().Be("outside-original");
    }

    /// <summary>
    /// The "P01" variant: <c>d</c> does not exist yet, so this call's own <see cref="PinnedDirectory.CreateChild"/>
    /// would create it — but the seam fires before that, so a link to <paramref name="outside"/> occupies the name
    /// first. <paramref name="outside"/> already has its own, unrelated, non-empty <c>e</c> subdirectory: the
    /// review's point is that a path-based cleanup, following the same swapped name a second time, could delete or
    /// write into it. This call's cleanup never re-resolves a path, so it cannot reach <paramref name="outside"/>'s
    /// <c>e</c> at all, whatever is inside it.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_of_a_new_ancestor_during_its_own_creation_touches_nothing_outside()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(outside, "e"));
        File.WriteAllText(Path.Combine(outside, "e", "keep.txt"), "outside-original");

        tools.BeforeOpenForTesting = _ =>
        {
            if (!Directory.Exists(Path.Combine(root, "d")))
            {
                CreateDirectoryLinkOrSkip(Path.Combine(root, "d"), outside);
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "d/e/new.cs", "hijack");

        result.Should().StartWith("error:");
        File.ReadAllText(Path.Combine(outside, "e", "keep.txt")).Should().Be("outside-original");
        File.Exists(Path.Combine(outside, "e", "new.cs")).Should().BeFalse();
    }

    /// <summary>
    /// "P01" itself: <c>d</c> and <c>e</c> are both genuinely created by this call, for real — the chain build sees
    /// no swap at all — and only once both are pinned does <c>d</c> get swapped for a link to
    /// <paramref name="outside"/>, whose own, unrelated, <em>empty</em> <c>e</c> subdirectory the review's
    /// <c>Directory.Delete("root/d/e")</c> bug reached by following the swapped name a second time, deleting it. A
    /// fixed cleanup removes <c>d</c> and <c>e</c> through the handles this call already holds, never by
    /// re-resolving <c>"root/d/e"</c>, so it cannot reach <paramref name="outside"/> at all. On Windows, pinning
    /// <c>d</c> with a share mode that excludes <c>FILE_SHARE_DELETE</c> means the swap this probe wants cannot even
    /// be performed; ruling (g): that is asserted directly, as the OS refusing the swap with an
    /// <see cref="IOException"/>, rather than silently swallowed. The race itself is reliably constructible on
    /// Linux, where an open descriptor does not pin a directory's name this way.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_of_an_ancestor_right_before_cleanup_does_not_delete_outside()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(outside, "e"));

        tools.BeforeOpenForTesting = candidate =>
        {
            if (!candidate.EndsWith("new.cs", StringComparison.Ordinal))
            {
                return; // only swap once d/e are genuinely created, right before the leaf itself opens
            }

            var swap = () =>
            {
                Directory.Delete(Path.Combine(root, "d"), recursive: true);
                CreateDirectoryLinkOrSkip(Path.Combine(root, "d"), outside);
            };

            if (OperatingSystem.IsWindows())
            {
                // Ruling (m): no leaf is open yet, so only the pins on d and d/e can refuse the swap.
                SwapIsRefusedWithASharingViolation(swap);
                return;
            }

            swap();
        };

        await tools.WriteFile(Caller(RunId), "d/e/new.cs", "hijack");

        Directory.Exists(Path.Combine(outside, "e")).Should().BeTrue();
    }

    /// <summary>
    /// A variant of the "P01"/"P02" probes with <c>d</c> already present, so the chain build takes
    /// <see cref="PinnedDirectory.CreateChild"/>'s already-occupied branch — <c>CreateDirectoryW</c> failing because
    /// something is there, falling back to opening it — rather than the genuinely-new branch, exercising both.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_of_an_existing_ancestor_during_chain_creation_touches_nothing_outside()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(outside, "e"));
        File.WriteAllText(Path.Combine(outside, "e", "keep.txt"), "outside-original");
        Directory.CreateDirectory(Path.Combine(root, "d"));

        tools.BeforeOpenForTesting = _ =>
        {
            if (Directory.Exists(Path.Combine(root, "d")) && new DirectoryInfo(Path.Combine(root, "d")).LinkTarget is null)
            {
                Directory.Delete(Path.Combine(root, "d"));
                CreateDirectoryLinkOrSkip(Path.Combine(root, "d"), outside);
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "d/e/new.cs", "hijack");

        result.Should().StartWith("error:");
        File.ReadAllText(Path.Combine(outside, "e", "keep.txt")).Should().Be("outside-original");
        File.Exists(Path.Combine(outside, "e", "new.cs")).Should().BeFalse();
    }

    /// <summary>
    /// Round-3 finding B3: on Linux, <c>PinnedFile.RealPath</c> was computed via <c>Path.Combine</c> instead of
    /// read from the fd, so the post-open check was a no-op. The review's own probe: renaming the pinned
    /// intermediate directory <c>a</c> to outside the workspace at the leaf seam still returned "wrote 6 bytes" and
    /// created the file under the renamed location. Ruling (d): every level, and the leaf file, are verified by
    /// reading <c>/proc/self/fd</c> back against the expected canonical path, so a rename is caught the same way a
    /// swap is — closing the gap and correcting round 2's report and commit, which called Linux's fd-relative opens
    /// "inherently immune to a later swap"; a rename of an already-pinned ancestor is exactly the swap they were
    /// not immune to. Linux-only: renaming a directory an open handle still refers to is exactly what Windows' own
    /// pinning (<c>FILE_SHARE_DELETE</c> excluded) already refuses outright, covered by the <c>IOException</c>
    /// assertions above; this probe is only constructible on Linux, where an open fd does not pin a name this way.
    /// </summary>
    [SkippableFact]
    public async Task A_rename_of_an_intermediate_directory_is_caught_before_commit()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "an open fd only survives a rename-away on Linux");
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.Delete(outside); // Directory.Move refuses an already-existing destination.
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));

        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("new.cs", StringComparison.Ordinal))
            {
                Directory.Move(Path.Combine(root, "a"), outside);
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "a/b/new.cs", "hijack");

        result.Should().StartWith("error:");
        File.Exists(Path.Combine(outside, "b", "new.cs")).Should().BeFalse();
    }

    /// <summary>
    /// The root-level variant of the same finding: renaming the workspace root itself, once pinned, must be caught
    /// the same way. Linux-only, for the same reason as above.
    /// </summary>
    [SkippableFact]
    public async Task A_rename_of_the_workspace_root_itself_is_caught_before_commit()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "an open fd only survives a rename-away on Linux");
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.Delete(outside);

        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("x.cs", StringComparison.Ordinal))
            {
                Directory.Move(root, outside);
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "x.cs", "hijack");

        result.Should().StartWith("error:");
        File.Exists(Path.Combine(outside, "x.cs")).Should().BeFalse();
    }

    /// <summary>
    /// Round-4 finding N2, ruling (n): <c>a</c> is renamed out of the workspace just before this call creates
    /// <c>a/b</c>. <c>mkdirat</c> works relative to <c>a</c>'s descriptor, so it creates <c>b</c> under the renamed
    /// directory, outside the workspace. The level's verification then fails, and the level must be removed
    /// through the same descriptor, not left there. Linux-only: on Windows the pin on <c>a</c> refuses the rename.
    /// </summary>
    [SkippableFact]
    public async Task A_rename_before_a_new_level_is_created_leaves_no_directory_outside()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "an open fd only survives a rename-away on Linux");
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.Delete(outside); // Directory.Move refuses an already-existing destination.
        Directory.CreateDirectory(Path.Combine(root, "a"));
        tools.BeforeOpenForTesting = candidate =>
        {
            if (string.Equals(candidate, "b", StringComparison.Ordinal))
            {
                Directory.Move(Path.Combine(root, "a"), outside);
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "a/b/new.cs", "hijack");

        using var scope = new AssertionScope();
        result.Should().StartWith("error:");
        Directory.Exists(Path.Combine(outside, "b")).Should().BeFalse();
    }

    /// <summary>
    /// Round-4 finding N4, ruling (d): the leaf's path is verified when it is opened and again right before the
    /// content is committed. Here <c>a</c> is renamed out of the workspace between the two, through
    /// <see cref="WorkspaceTools.BeforeCommitForTesting"/>, so only the second verification can refuse the write, and
    /// the file this call created is then removed through <c>a</c>'s descriptor. Linux-only: on Windows the pin on
    /// <c>a</c> refuses the rename.
    /// </summary>
    [SkippableFact]
    public async Task A_rename_between_open_and_commit_refuses_the_write()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "an open fd only survives a rename-away on Linux");
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.Delete(outside);
        Directory.CreateDirectory(Path.Combine(root, "a"));
        tools.BeforeCommitForTesting = () => Directory.Move(Path.Combine(root, "a"), outside);

        var result = await tools.WriteFile(Caller(RunId), "a/new.cs", "hijack");

        using var scope = new AssertionScope();
        result.Should().StartWith("error:");
        File.Exists(Path.Combine(outside, "new.cs")).Should().BeFalse();
    }

    /// <summary>The <c>edit_file</c> side of the test above: the edit is refused and the moved file keeps its content.</summary>
    [SkippableFact]
    public async Task A_rename_between_open_and_commit_refuses_the_edit()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "an open fd only survives a rename-away on Linux");
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.Delete(outside);
        Directory.CreateDirectory(Path.Combine(root, "a"));
        File.WriteAllText(Path.Combine(root, "a", "f.cs"), "old");
        tools.BeforeCommitForTesting = () => Directory.Move(Path.Combine(root, "a"), outside);

        var result = await tools.EditFile(Caller(RunId), "a/f.cs", "old", "new");

        using var scope = new AssertionScope();
        result.Should().StartWith("error:");
        File.ReadAllText(Path.Combine(outside, "f.cs")).Should().Be("old");
    }

    /// <summary>
    /// The review's third finding: a disallowed-extension write must leave no trace, including the parent
    /// directories it created to get there. <c>d</c> and <c>d/e</c> do not exist beforehand. The pre-check refuses
    /// this before anything is created, so the test below covers the cleanup itself.
    /// </summary>
    [Fact]
    public async Task A_disallowed_extension_write_leaves_no_directories_behind()
    {
        var (tools, _, root) = Build();

        var result = await tools.WriteFile(Caller(RunId), "d/e/Makefile", "x");

        result.Should().Contain("extension ''");
        Directory.Exists(Path.Combine(root, "d", "e")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "d")).Should().BeFalse();
    }

    /// <summary>
    /// Round-4 finding N4: a refusal after the leaf is open must remove every level this call created, not only the
    /// innermost. The allow-list is narrowed after the pre-check passed, so <c>x</c>, <c>x/y</c> and <c>x/y/z</c> are
    /// all created before the post-check refuses the write.
    /// </summary>
    [Fact]
    public async Task A_refusal_after_the_open_removes_every_level_this_call_created()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        var (tools, _, root) = Build(allowedWriteExtensions: allowed);
        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("new.cs", StringComparison.Ordinal))
            {
                allowed.Clear();
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "x/y/z/new.cs", "x");

        using var scope = new AssertionScope();
        result.Should().Contain("extension");
        Directory.Exists(Path.Combine(root, "x", "y", "z")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "x", "y")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "x")).Should().BeFalse();
    }

    /// <summary>
    /// One of the review's minors: a check-then-create race must not record a directory this call did not create —
    /// including simply finding it already there — as its own. <c>sub</c> pre-exists; the refusal comes from
    /// narrowing the allow-list after the pre-check, so the chain build itself takes the ordinary,
    /// nothing-swapped path for an already-occupied name.
    /// </summary>
    [Fact]
    public async Task A_disallowed_write_never_removes_a_directory_it_did_not_create()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        var (tools, _, root) = Build(allowedWriteExtensions: allowed);
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("new.cs", StringComparison.Ordinal))
            {
                allowed.Clear();
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "sub/new.cs", "hijack");

        result.Should().Contain("extension");
        Directory.Exists(Path.Combine(root, "sub")).Should().BeTrue();
    }

    /// <summary>
    /// Another of the review's minors: an exception partway through pinning the chain — rather than an ordinary
    /// refusal — must still release every already-pinned ancestor, and, per round-3 ruling (i), remove every level
    /// this call created, exactly as an ordinary chain-build <em>Result</em> failure does. <c>d</c> is genuinely
    /// created by this call before the seam throws for <c>d/e</c>, so a fixed <c>PinDirectoryChain</c> both releases
    /// <c>d</c>'s handle and removes <c>d</c> itself; <see cref="Directory.Exists"/> returning <see langword="false"/>
    /// proves both at once — if a handle had leaked instead, on Windows the removal attempt inside the same catch
    /// block would itself fail (a sharing violation against its own leaked, non-delete-shared handle), leaving
    /// <c>d</c> behind.
    /// </summary>
    [Fact]
    public async Task An_exception_mid_chain_still_releases_pinned_handles()
    {
        var (tools, _, root) = Build();
        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith('e'))
            {
                throw new InvalidOperationException("boom");
            }
        };

        // Ruling (g): "the mid-chain test on Linux asserts that the /proc/self/fd count returns to its baseline" —
        // a leaked fd on Linux would not block the Directory.Exists check above, unlike a leaked Windows handle.
        // Ruling (o): only descriptors whose target is under this test's root count, so other tests running in
        // parallel cannot move the number.
        var baselineFds = OperatingSystem.IsLinux() ? CountOpenFileDescriptorsUnder(root) : -1;

        var act = () => tools.WriteFile(Caller(RunId), "d/e/f.cs", "x");

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(Path.Combine(root, "d")).Should().BeFalse();

        if (OperatingSystem.IsLinux())
        {
            CountOpenFileDescriptorsUnder(root).Should().Be(baselineFds);
        }
    }

    private static int CountOpenFileDescriptorsUnder(string root) =>
        Directory.EnumerateFileSystemEntries("/proc/self/fd").Count(fd => LinkTargetOf(fd) is { } target
            && (string.Equals(target, root, StringComparison.Ordinal) || target.StartsWith(root + "/", StringComparison.Ordinal)));

    /// <summary>The target of one <c>/proc/self/fd</c> entry, or <see langword="null"/> when that descriptor closed while the directory was being read.</summary>
    private static string? LinkTargetOf(string fd)
    {
        try
        {
            return new FileInfo(fd).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Ruling (i)'s "ordinary Result failure" branch in PinDirectoryChain calls the exact same
    // RemoveCreatedDirectories(chain); DisposeChain(chain); pair as the catch block the test above exercises, on
    // the same chain shape (chain = [root, d], failure reaching "e"). A dedicated scenario for that branch was not
    // added: every construction tried left a foreign entry (the swapped-in link itself) sitting inside "d", which
    // makes "d" genuinely, correctly non-empty — refusing to remove it there is the right behaviour, not a gap —
    // so there was no way to reach this branch without producing exactly the debris ruling (i) is not about.

    [Fact]
    public async Task Edit_requires_exactly_one_match()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "a.cs"), "x x");

        (await tools.EditFile(Caller(RunId), "a.cs", "x", "y")).Should().Contain("2");
        (await tools.EditFile(Caller(RunId), "a.cs", "z", "y")).Should().Contain("0");
        (await tools.EditFile(Caller(RunId), "a.cs", "x x", "y")).Should().StartWith("edited");
    }

    [Fact]
    public async Task List_files_skips_the_git_directory()
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "x");

        (await tools.ListFiles(Caller(RunId))).Should().NotContain(".git");
    }

    /// <summary>
    /// The review's fifth finding: the old walk checked whether an entry was a reparse point, then separately
    /// enumerated it as a subdirectory — two calls, with a gap between them a swap could use to make the second one
    /// list an outside directory's names instead. This walk enumerates each directory in one handle-based call and
    /// descends by opening the child relative to the same pinned parent, so the seam fires right where the old code
    /// would have made its second, separate call — and finds nothing to reach, since the child no longer resolves
    /// where the walk expects.
    /// </summary>
    [SkippableFact]
    public async Task A_swap_during_the_walk_never_lists_outside_names()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        File.WriteAllText(Path.Combine(outside, "secret.cs"), "outside-original");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"));
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
        };

        var listing = await tools.ListFiles(Caller(RunId));

        listing.Should().NotContain("secret");
        File.ReadAllText(Path.Combine(outside, "secret.cs")).Should().Be("outside-original");
    }

    // Ruling R29. Build() uses AllowedWriteExtensions = [".cs", ".md"] unless a test passes its own.
    [Theory]
    [InlineData("Directory.Build.props", false)]
    [InlineData("x.csproj", false)]
    [InlineData("x.targets", false)]
    [InlineData("Makefile", false)] // no extension
    [InlineData("x.CS", true)] // case-insensitive
    [InlineData("sub/x.cs", true)]
    public async Task Only_allow_listed_extensions_can_be_written(string path, bool allowed)
    {
        var (tools, _, root) = Build();
        var written = await tools.WriteFile(Caller(RunId), path, "content");

        written.StartsWith("wrote", StringComparison.Ordinal).Should().Be(allowed, written);
        File.Exists(Path.Combine(root, path)).Should().Be(allowed);
        if (!allowed)
        {
            written.Should().Contain($"extension '{Path.GetExtension(path)}'");
        }
    }

    [Fact]
    public async Task Edit_is_refused_for_a_disallowed_extension_and_leaves_the_file()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "x.csproj"), "<Project />");

        (await tools.EditFile(Caller(RunId), "x.csproj", "<Project />", "<Project><Target Name=\"X\" /></Project>")).Should().Contain("extension '.csproj'");
        File.ReadAllText(Path.Combine(root, "x.csproj")).Should().Be("<Project />");
    }

    [SkippableFact]
    public async Task A_link_named_like_an_allowed_file_cannot_smuggle_a_disallowed_one()
    {
        var (tools, _, root) = Build();
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        CreateFileSymlinkOrSkip(Path.Combine(root, "alias.cs"), Path.Combine(root, "Directory.Build.props"));

        (await tools.WriteFile(Caller(RunId), "alias.cs", "<Project><Target Name=\"X\" /></Project>")).Should().Contain("extension '.props'");
        File.ReadAllText(Path.Combine(root, "Directory.Build.props")).Should().Be("<Project />");
    }

    [Fact]
    public async Task An_empty_allow_list_refuses_every_write()
    {
        var (tools, _, root) = Build(allowedWriteExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        (await tools.WriteFile(Caller(RunId), "a.cs", "class A {}")).Should().StartWith("error: extension '.cs'");
        File.Exists(Path.Combine(root, "a.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task A_callers_grant_claim_narrows_the_allow_list()
    {
        var (tools, _, _) = Build();
        var caller = Caller(RunId, writeExtensions: ".md"); // adds the RunWorkspaceClaims.WriteExtensions claim

        (await tools.WriteFile(caller, "a.cs", "class A {}")).Should().Contain("extension '.cs'");
        (await tools.WriteFile(caller, "notes.md", "x")).Should().StartWith("wrote");
    }

    /// <summary>Red: keep the old exact-match IsProtected.</summary>
    [Fact]
    public async Task Write_is_refused_under_a_protected_directory_prefix()
    {
        var (tools, _, root) = Build(protectedPaths: [".github/"]);

        var result = await tools.WriteFile(Caller(RunId), ".github/workflows/x.yml", "x");

        result.Should().StartWith("error:").And.Contain("protected");
        File.Exists(Path.Combine(root, ".github", "workflows", "x.yml")).Should().BeFalse();
    }

    /// <summary>Red: ignore AllowAnyWriteExtension.</summary>
    [Fact]
    public async Task Any_extension_is_writable_when_the_ceiling_allows_any()
    {
        var (tools, _, root) = Build(allowedWriteExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase), allowAnyWriteExtension: true);

        (await tools.WriteFile(Caller(RunId), "Lib.csproj", "<Project />")).Should().StartWith("wrote");
        File.Exists(Path.Combine(root, "Lib.csproj")).Should().BeTrue();
    }

    /// <summary>Red: return early on AllowAnyWriteExtension before the claim check.</summary>
    [Fact]
    public async Task A_callers_extension_claim_still_narrows_an_any_ceiling()
    {
        var (tools, _, root) = Build(allowedWriteExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase), allowAnyWriteExtension: true);

        (await tools.WriteFile(Caller(RunId, writeExtensions: ".cs"), "Lib.csproj", "<Project />")).Should().StartWith("error: extension '.csproj'");
        File.Exists(Path.Combine(root, "Lib.csproj")).Should().BeFalse();
    }

    /// <summary>Red: refuse whenever a claim is present, even when the claim lists the extension.</summary>
    [Fact]
    public async Task A_callers_extension_claim_still_permits_what_it_lists_under_an_any_ceiling()
    {
        var (tools, _, root) = Build(allowedWriteExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase), allowAnyWriteExtension: true);

        (await tools.WriteFile(Caller(RunId, writeExtensions: ".cs"), "Lib.cs", "class A {}")).Should().StartWith("wrote");
        File.Exists(Path.Combine(root, "Lib.cs")).Should().BeTrue();
    }

    /// <summary>Red: the overload leaves AllowAnyWriteExtension false.</summary>
    [Fact]
    public async Task The_allowing_any_extension_overload_registers_an_any_ceiling()
    {
        var services = new ServiceCollection();
        services.AddThalos(t => t.UseRunWorkspaceToolsAllowingAnyExtension(o => o.ProtectedPaths.Add(".github/")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<RunWorkspaceToolOptions>();
        var root = NewTempDir("thalos-workspace-tools-");
        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", root, null);
        var tools = new WorkspaceTools(new FakeRunWorkspaceProvider(workspace), options, [new FakeChangeListener()], NullLogger<WorkspaceTools>.Instance);

        options.AllowAnyWriteExtension.Should().BeTrue();
        (await tools.WriteFile(Caller(RunId), "Lib.csproj", "<Project />")).Should().StartWith("wrote");
        (await tools.WriteFile(Caller(RunId), ".github/x.yml", "x")).Should().Contain("protected");
    }

    /// <summary>A claim that is present but blank is a grant of zero extensions, not "no grant" — only an absent claim falls back to the ceiling.</summary>
    [Fact]
    public async Task A_blank_grant_claim_refuses_every_write()
    {
        var (tools, _, root) = Build();
        var caller = Caller(RunId, writeExtensions: " ");

        (await tools.WriteFile(caller, "a.cs", "class A {}")).Should().StartWith("error: extension '.cs'");
        File.Exists(Path.Combine(root, "a.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task Read_file_refuses_a_file_over_the_configured_size_limit()
    {
        var (tools, _, root) = Build(maxReadBytes: 4);
        File.WriteAllText(Path.Combine(root, "big.md"), "more than four bytes");

        (await tools.ReadFile(Caller(RunId), "big.md")).Should().StartWith("error:");
    }

    /// <summary>Unlike the old, unbounded <c>ReadToEndAsync</c>, <c>edit_file</c> now caps its read the same way <c>read_file</c> does.</summary>
    [Fact]
    public async Task Edit_file_refuses_a_file_over_the_configured_size_limit()
    {
        var (tools, _, root) = Build(maxReadBytes: 4);
        File.WriteAllText(Path.Combine(root, "big.md"), "more than four bytes");

        (await tools.EditFile(Caller(RunId), "big.md", "more", "less")).Should().StartWith("error:");
    }

    [Fact]
    public async Task List_files_caps_the_number_of_entries_at_the_configured_limit()
    {
        var (tools, _, root) = Build(maxListEntries: 2);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(root, $"f{i}.cs"), "x");
        }

        var listing = await tools.ListFiles(Caller(RunId));

        listing.Split('\n').Count(line => line.EndsWith(".cs", StringComparison.Ordinal)).Should().Be(2);
    }

    /// <summary>
    /// Simulates the check-to-use race the type-level remarks on <see cref="WorkspaceTools"/> describe: between
    /// <see cref="WorkspacePath.Resolve"/> succeeding for "sub/escape.cs" (a plain, real, in-workspace file at that
    /// moment) and the tool's own open, "sub" itself is swapped for a link to a directory outside the workspace —
    /// via the instance-level <see cref="WorkspaceTools.BeforeOpenForTesting"/> seam, so the race is deterministic
    /// rather than timing-dependent. Unfixed, the open would follow the swapped link and edit the outside file in
    /// place.
    /// </summary>
    [SkippableFact]
    public async Task A_directory_swapped_for_a_link_between_resolve_and_open_is_refused_without_touching_the_outside_file()
    {
        var (tools, _, root) = Build();
        var outside = NewTempDir("thalos-workspace-tools-outside-");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "escape.cs"), "outside-original");
        File.WriteAllText(Path.Combine(outside, "escape.cs"), "outside-original");

        tools.BeforeOpenForTesting = _ =>
        {
            Directory.Delete(Path.Combine(root, "sub"), recursive: true);
            CreateDirectoryLinkOrSkip(Path.Combine(root, "sub"), outside);
        };

        var result = await tools.EditFile(Caller(RunId), "sub/escape.cs", "outside-original", "malicious");

        result.Should().Contain("not permitted");
        File.ReadAllText(Path.Combine(outside, "escape.cs")).Should().Be("outside-original");
    }

    /// <summary>A listener that throws must never turn an otherwise-successful write into a failed tool call.</summary>
    [Fact]
    public async Task A_throwing_listener_still_leaves_the_write_successful()
    {
        var (tools, _, _) = Build(extraListener: static _ => new ThrowingChangeListener());

        var act = () => tools.WriteFile(Caller(RunId), "a.cs", "class A {}");

        (await act.Should().NotThrowAsync()).Which.Should().StartWith("wrote");
    }

    /// <summary>
    /// Notify must run only after the write's own handle is disposed — a listener that reads the file back through
    /// an ordinary, non-exclusive open must not collide with a still-open, <see cref="FileShare.None"/> handle.
    /// </summary>
    [Fact]
    public async Task A_reading_listener_can_read_the_file_the_write_just_produced()
    {
        ReadingChangeListener? reading = null;
        var (tools, _, _) = Build(extraListener: root =>
        {
            reading = new ReadingChangeListener(root);
            return reading;
        });

        (await tools.WriteFile(Caller(RunId), "a.cs", "class A {}")).Should().StartWith("wrote");

        reading!.ReadBack.Should().Be("class A {}");
    }

    /// <summary>Listeners receive the changed path relative to the canonical root, with forward-slash separators, regardless of the OS.</summary>
    [Fact]
    public async Task A_listener_receives_a_root_relative_forward_slash_path()
    {
        var (tools, listener, _) = Build();

        (await tools.WriteFile(Caller(RunId), "sub/deep/File.cs", "x")).Should().StartWith("wrote");

        listener.Changes.Should().ContainSingle().Which.Path.Should().Be("sub/deep/File.cs");
    }

    /// <summary>
    /// A workspace root reached through a junction must work exactly like one reached directly: every comparison
    /// against the root must canonicalise it first, with the same kernel canonicalisation <see cref="WorkspacePath.Resolve"/>
    /// itself applies.
    /// </summary>
    [SkippableFact]
    public async Task A_root_reached_through_a_junction_works()
    {
        var real = NewTempDir("thalos-workspace-tools-real-");
        var linkParent = NewTempDir("thalos-workspace-tools-link-parent-");
        var throughLink = Path.Combine(linkParent, "root-link");
        CreateDirectoryLinkOrSkip(throughLink, real);

        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", throughLink, null);
        var provider = new FakeRunWorkspaceProvider(workspace);
        var options = new RunWorkspaceToolOptions { AllowedWriteExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" } };
        var tools = new WorkspaceTools(provider, options, [], NullLogger<WorkspaceTools>.Instance);

        (await tools.WriteFile(Caller(RunId), "a.cs", "class A {}")).Should().StartWith("wrote");
        (await tools.ReadFile(Caller(RunId), "a.cs")).Should().Be("class A {}");
        File.ReadAllText(Path.Combine(real, "a.cs")).Should().Be("class A {}");
    }

    /// <summary>
    /// Regression for the ancestor-pinning rewrite, tightened for round-3 finding B1: the review measured this
    /// exact scenario with truly parallel <see cref="Task.Run(Action)"/> callers and got only 103 of 300 "wrote"
    /// results, the rest sharing-violation refusals, because every pin requested <c>DELETE</c> access it did not
    /// need. Ruling (c): same-file writers serialize instead — a bounded wait on contention, last writer wins — so
    /// every one of these 300 truly parallel writers must get "wrote", never a refusal, and the file must exist
    /// afterwards with content from one of them.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_to_the_same_file_never_throw_and_never_lose_the_file()
    {
        var (tools, _, root) = Build(contentionTimeout: SameFileQueueTimeout);
        var caller = Caller(RunId);

        var tasks = Enumerable.Range(0, 300).Select(i => Task.Run(() => tools.WriteFile(caller, "shared.cs", $"content-{i}")));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.StartsWith("wrote", StringComparison.Ordinal));
        File.Exists(Path.Combine(root, "shared.cs")).Should().BeTrue();
        Enumerable.Range(0, 300).Select(i => $"content-{i}").Should().Contain(File.ReadAllText(Path.Combine(root, "shared.cs")));
    }

    /// <summary>
    /// A second concurrency regression specific to this round, also tightened for finding B1 (measured as 4 of 50
    /// "wrote" under truly parallel <see cref="Task.Run(Action)"/>): many writers racing to create the very same
    /// new ancestor directory — <see cref="PinnedDirectory.CreateChild"/>'s already-occupied fallback — must all
    /// succeed cleanly, each still landing its own distinct file. Ruling (c): "writers into the same directories all
    /// succeed", no busy result, no refusal.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_creating_the_same_new_directory_never_throw()
    {
        var (tools, _, root) = Build();
        var caller = Caller(RunId);

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() => tools.WriteFile(caller, $"newdir/f{i}.cs", $"content-{i}")));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.StartsWith("wrote", StringComparison.Ordinal));
        for (var i = 0; i < 50; i++)
        {
            File.Exists(Path.Combine(root, "newdir", $"f{i}.cs")).Should().BeTrue();
        }
    }

    /// <summary>
    /// The third scenario the review measured directly (7 of 50 "wrote" under truly parallel
    /// <see cref="Task.Run(Action)"/>, against an already-existing directory rather than one being created): many
    /// writers of distinct files into one pre-existing directory must all succeed, since none of their directory
    /// pins request <c>DELETE</c> access and so none of them can collide with each other (ruling (b)).
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_of_distinct_files_in_an_existing_directory_never_throw()
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, "existing"));
        var caller = Caller(RunId);

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() => tools.WriteFile(caller, $"existing/f{i}.cs", $"content-{i}")));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.StartsWith("wrote", StringComparison.Ordinal));
        for (var i = 0; i < 50; i++)
        {
            File.Exists(Path.Combine(root, "existing", $"f{i}.cs")).Should().BeTrue();
        }
    }

    /// <summary>
    /// Round-5 finding 1, the reviewer's deterministic reproduction. Writer A creates <c>d</c>, is refused after the
    /// open because its allow-list is narrowed once the pre-check passed, and parks at the cleanup seam. Writer B then
    /// pins the existing <c>d</c> and parks right before it opens <c>d/y.cs</c>. A is released and cleans up; B is
    /// released and writes. On Linux, <c>unlinkat</c> with <c>AT_REMOVEDIR</c> removed the empty <c>d</c> under B's
    /// pin, so B's create failed and B got the generic refusal. Ruling (t): A's cleanup sees B's count on <c>d</c> in
    /// the level table and leaves it, so B's write succeeds. On Windows B's pin already refused A's delete by share
    /// mode, so this test was green there before the fix; it runs on both.
    /// </summary>
    [Fact]
    public async Task A_cleanup_never_removes_a_directory_another_writer_still_holds()
    {
        var allowedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        var (writerA, _, root) = Build(allowedWriteExtensions: allowedA);
        var writerB = ToolsOver(root);
        using var aParked = new SemaphoreSlim(0);
        using var releaseA = new SemaphoreSlim(0);
        using var bParked = new SemaphoreSlim(0);
        using var releaseB = new SemaphoreSlim(0);
        writerA.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("x.cs", StringComparison.Ordinal))
            {
                allowedA.Clear(); // A's post-check, after this open, now refuses
            }
        };
        writerA.BeforeCleanupForTesting = () =>
        {
            aParked.Release();
            releaseA.Wait(TimeSpan.FromSeconds(30));
        };
        writerB.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("y.cs", StringComparison.Ordinal))
            {
                bParked.Release();
                releaseB.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var a = Task.Run(() => writerA.WriteFile(Caller(RunId), "d/x.cs", "a"));
        await SignalledOrThrowAsync(aParked, "A never reached its cleanup");
        var b = Task.Run(() => writerB.WriteFile(Caller(RunId), "d/y.cs", "b"));
        await SignalledOrThrowAsync(bParked, "B never pinned d and reached its leaf");
        releaseA.Release();
        var resultA = await a;
        releaseB.Release();
        var resultB = await b;

        using var scope = new AssertionScope();
        resultA.Should().Contain("extension '.cs'");
        resultB.Should().Be("wrote 1 bytes to 'd/y.cs'.");
        var written = Path.Combine(root, "d", "y.cs");
        (File.Exists(written) ? File.ReadAllText(written) : "(missing)").Should().Be("b");
    }

    /// <summary>
    /// Round-5 finding 1 by timing alone, the reviewer's hammer: in each of 1,000 pairs, writer A creates a new
    /// directory and is refused after the open, so it removes that directory, while writer B writes a file into the
    /// same directory at the same moment. Before ruling (t), 2 to 7 of B's writes in 1,000 were refused on Linux.
    /// Every one of B's writes must succeed and land.
    /// </summary>
    [Fact]
    public async Task A_thousand_cleanups_racing_writers_into_the_same_new_directory_refuse_none()
    {
        var (writerB, _, root) = Build();
        var caller = Caller(RunId);
        var refusedB = new List<string>();
        var unrefusedA = new List<string>();

        for (var i = 0; i < 1000; i++)
        {
            var allowedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
            var writerA = ToolsOver(root, allowedA);
            writerA.BeforeOpenForTesting = candidate =>
            {
                if (candidate.EndsWith("a.cs", StringComparison.Ordinal))
                {
                    allowedA.Clear();
                }
            };

            var directory = $"d{i}";
            var a = Task.Run(() => writerA.WriteFile(caller, $"{directory}/a.cs", "a"));
            var b = Task.Run(() => writerB.WriteFile(caller, $"{directory}/b.cs", "b"));
            var (resultA, resultB) = (await a, await b);
            if (!resultA.Contains("extension", StringComparison.Ordinal))
            {
                unrefusedA.Add(resultA);
            }

            if (!resultB.StartsWith("wrote", StringComparison.Ordinal) || !File.Exists(Path.Combine(root, directory, "b.cs")))
            {
                refusedB.Add($"{directory}: {resultB}");
            }
        }

        using var scope = new AssertionScope();
        unrefusedA.Should().BeEmpty("every A is refused after its open, so every A runs its cleanup");
        refusedB.Should().BeEmpty();
    }

    /// <summary>
    /// Ruling (t)'s bookkeeping: every level a chain counts is released again, whatever the call did — wrote, read,
    /// edited, listed, or was refused after the open and cleaned up. After 2,000 such calls on one instance with its
    /// own table, the table is empty, and on Linux no descriptor under the workspace is left open.
    /// </summary>
    [Fact]
    public async Task The_directory_level_table_is_empty_again_after_two_thousand_mixed_calls()
    {
        var (tools, _, root) = Build();
        var levels = new DirectoryLevelTable();
        tools.DirectoryLevels = levels;
        var allowedR = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        var refuser = ToolsOver(root, allowedR);
        refuser.DirectoryLevels = levels;
        refuser.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith("refused.cs", StringComparison.Ordinal))
            {
                allowedR.Clear();
            }
        };
        var caller = Caller(RunId);
        var baselineFds = OperatingSystem.IsLinux() ? CountOpenFileDescriptorsUnder(root) : -1;
        var unexpected = new List<string>();

        for (var i = 0; i < 400; i++)
        {
            var file = $"a{i % 7}/b{i % 3}/f{i}.cs";
            allowedR.Add(".cs");
            string[] results =
            [
                await tools.WriteFile(caller, file, "old"),
                await tools.ReadFile(caller, file),
                await tools.EditFile(caller, file, "old", "new"),
                await tools.ListFiles(caller, $"a{i % 7}"),
                await refuser.WriteFile(caller, $"r{i}/q/refused.cs", "x"),
            ];
            if (!results[0].StartsWith("wrote", StringComparison.Ordinal) || !string.Equals(results[1], "old", StringComparison.Ordinal)
                || !results[2].StartsWith("edited", StringComparison.Ordinal) || results[3].StartsWith("error", StringComparison.Ordinal)
                || !results[4].Contains("extension", StringComparison.Ordinal))
            {
                unexpected.Add(string.Join(" | ", results));
            }
        }

        using var scope = new AssertionScope();
        unexpected.Should().BeEmpty();
        levels.Count.Should().Be(0);
        if (OperatingSystem.IsLinux())
        {
            CountOpenFileDescriptorsUnder(root).Should().Be(baselineFds);
        }
    }

    /// <summary>
    /// Round-5 ruling (t), a holder outside the process: <c>d</c> is held open with <c>DELETE</c> access and a share
    /// mode without <c>FILE_SHARE_DELETE</c>, as another process's removal of it would. Opening <c>d</c> then fails
    /// with a sharing violation. The call must release its chain and walk again from the root, and the holder lets go
    /// on the second attempt to open <c>d</c>, inside the wait by construction. Before the fix a write got the generic
    /// refusal, a read or an edit got busy at once, and a listing got "is not a directory". Windows-only: Linux has no
    /// mandatory sharing mode.
    /// </summary>
    [SkippableTheory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("edit")]
    [InlineData("list")]
    public async Task A_directory_an_outside_holder_is_deleting_is_waited_for_then_used(string operation)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are a Windows-enforced concept");
        var (tools, _, root) = Build(contentionTimeout: TimeSpan.FromSeconds(30));
        var directory = Path.Combine(root, "d");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "f.cs"), "original");
        using var holder = HoldForDeletion(directory);
        var directoryAttempts = 0;
        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith(Path.DirectorySeparatorChar + "d", StringComparison.Ordinal) && ++directoryAttempts == 2)
            {
                holder.Dispose();
            }
        };

        var result = operation switch
        {
            "write" => await tools.WriteFile(Caller(RunId), "d/f.cs", "written"),
            "read" => await tools.ReadFile(Caller(RunId), "d/f.cs"),
            "edit" => await tools.EditFile(Caller(RunId), "d/f.cs", "original", "edited"),
            _ => await tools.ListFiles(Caller(RunId), "d"),
        };

        using var scope = new AssertionScope();
        directoryAttempts.Should().BeGreaterThanOrEqualTo(2, "the holder lets go on the second attempt, inside the wait");
        result.Should().Be(operation switch
        {
            "write" => "wrote 7 bytes to 'd/f.cs'.",
            "read" => "original",
            "edit" => "edited 'd/f.cs'.",
            _ => "f.cs",
        });
    }

    /// <summary>
    /// The bound on the same wait: an outside holder of <c>d</c> that never lets go ends in the busy text once the
    /// contention timeout is spent, not in a refusal, and not before the timeout. Windows-only, as above.
    /// </summary>
    [SkippableTheory]
    [InlineData("write")]
    [InlineData("read")]
    public async Task An_outside_holder_that_never_lets_go_of_a_directory_ends_in_busy_after_the_timeout(string operation)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are a Windows-enforced concept");
        var timeout = TimeSpan.FromMilliseconds(500);
        var (tools, _, root) = Build(contentionTimeout: timeout);
        var directory = Path.Combine(root, "d");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "f.cs"), "original");

        string result;
        var elapsed = Stopwatch.StartNew();
        using (HoldForDeletion(directory))
        {
            result = string.Equals(operation, "write", StringComparison.Ordinal)
                ? await tools.WriteFile(Caller(RunId), "d/f.cs", "written")
                : await tools.ReadFile(Caller(RunId), "d/f.cs");
            elapsed.Stop();
        }

        using var scope = new AssertionScope();
        result.Should().Be("error: the file is busy; try again.");
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(timeout - TimeSpan.FromMilliseconds(50), "the busy result comes only after retrying for the whole contention timeout");
    }

    /// <summary>
    /// Round-5 ruling (t), the Linux side of a holder outside the process: <c>d</c> is created and pinned by the call,
    /// and then removed from outside before <c>d/e</c> is created in it. <c>mkdirat</c> and <c>openat</c> in the
    /// removed <c>d</c> both fail with <c>ENOENT</c>. The call must walk again from the root, create both levels anew,
    /// and write. Before the fix it returned the generic refusal. Linux-only: on Windows the pin on <c>d</c> refuses
    /// the removal itself.
    /// </summary>
    [SkippableFact]
    public async Task A_new_directory_removed_from_outside_during_the_walk_is_created_again()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "on Windows the pin on d refuses the removal");
        var (tools, _, root) = Build();
        var attemptsAtE = 0;
        tools.BeforeOpenForTesting = candidate =>
        {
            if (string.Equals(Path.GetFileName(candidate), "e", StringComparison.Ordinal) && ++attemptsAtE == 1)
            {
                Directory.Delete(Path.Combine(root, "d"));
            }
        };

        var result = await tools.WriteFile(Caller(RunId), "d/e/f.cs", "x");

        using var scope = new AssertionScope();
        result.Should().Be("wrote 1 bytes to 'd/e/f.cs'.");
        File.Exists(Path.Combine(root, "d", "e", "f.cs")).Should().BeTrue();
        attemptsAtE.Should().Be(2);
    }

    /// <summary>
    /// Round-5 finding 2 and ruling (u): a write that returns busy because the per-path lock stayed held must remove
    /// the directories it created on the way, as every other result that writes nothing does. The test holds the lock
    /// itself, through the instance's own table, so no other chain holds <c>d</c> or <c>d/e</c>.
    /// </summary>
    [Fact]
    public async Task A_busy_write_leaves_no_directories_it_created()
    {
        var (tools, _, root) = Build(contentionTimeout: TimeSpan.FromMilliseconds(100));
        var locks = new LeafLockTable();
        tools.LeafLocks = locks;
        using var held = await locks.AcquireAsync(WorkspacePath.Resolve(root, "d/e/f.cs").Value, TimeSpan.Zero, CancellationToken.None);

        var result = await tools.WriteFile(Caller(RunId), "d/e/f.cs", "x");

        using var scope = new AssertionScope();
        result.Should().Be("error: the file is busy; try again.");
        Directory.Exists(Path.Combine(root, "d", "e")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "d")).Should().BeFalse();
    }

    /// <summary>
    /// Ruling (u)'s other path: a write cancelled while it waits for the per-path lock must remove the directory it
    /// created before the cancellation propagates. The token is cancelled from the seam as <c>f</c> is created, so
    /// the cancellation lands in the wait by construction.
    /// </summary>
    [Fact]
    public async Task A_write_cancelled_while_waiting_leaves_no_directories_it_created()
    {
        var (tools, _, root) = Build(contentionTimeout: TimeSpan.FromSeconds(30));
        var locks = new LeafLockTable();
        tools.LeafLocks = locks;
        using var held = await locks.AcquireAsync(WorkspacePath.Resolve(root, "f/g.cs").Value, TimeSpan.Zero, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        tools.BeforeOpenForTesting = candidate =>
        {
            if (string.Equals(Path.GetFileName(candidate), "f", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        var act = () => tools.WriteFile(Caller(RunId), "f/g.cs", "x", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(root, "f")).Should().BeFalse();
    }

    /// <summary>
    /// The breaker adjudication's M-b, the reviewer's probe: an outside holder of <c>sub</c>, with <c>DELETE</c>
    /// access and no <c>FILE_SHARE_DELETE</c>, made the walk's open of <c>sub</c> fail, and the walk skipped it. The
    /// listing showed <c>sub/</c> and <c>top.cs</c> with <c>sub/inner.cs</c> missing, and no error. Ruling (e):
    /// never a short listing. The walk must wait for the holder, which lets go on the second attempt to open
    /// <c>sub</c>, inside the wait by construction, and then list the whole tree. Windows-only: Linux has no mandatory
    /// sharing mode.
    /// </summary>
    [SkippableFact]
    public async Task A_subdirectory_an_outside_holder_has_is_waited_for_not_left_out_of_the_listing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are a Windows-enforced concept");
        var (tools, _, root) = Build(contentionTimeout: TimeSpan.FromSeconds(30));
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "inner.cs"), "x");
        File.WriteAllText(Path.Combine(root, "top.cs"), "x");
        using var holder = HoldForDeletion(sub);
        var subAttempts = 0;
        tools.BeforeOpenForTesting = candidate =>
        {
            if (candidate.EndsWith(Path.DirectorySeparatorChar + "sub", StringComparison.Ordinal) && ++subAttempts == 2)
            {
                holder.Dispose();
            }
        };

        var result = await tools.ListFiles(Caller(RunId));

        using var scope = new AssertionScope();
        subAttempts.Should().BeGreaterThanOrEqualTo(2, "the holder lets go on the second attempt, inside the wait");
        result.Should().Be("sub/\nsub/inner.cs\ntop.cs");
    }

    /// <summary>
    /// The bound on the same wait: an outside holder of <c>sub</c> that never lets go ends in the busy text once the
    /// contention timeout is spent, never a listing without <c>sub</c>'s contents. Windows-only, as above.
    /// </summary>
    [SkippableFact]
    public async Task A_subdirectory_an_outside_holder_never_lets_go_of_ends_the_listing_in_busy()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are a Windows-enforced concept");
        var timeout = TimeSpan.FromMilliseconds(500);
        var (tools, _, root) = Build(contentionTimeout: timeout);
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "inner.cs"), "x");

        string result;
        var elapsed = Stopwatch.StartNew();
        using (HoldForDeletion(sub))
        {
            result = await tools.ListFiles(Caller(RunId));
            elapsed.Stop();
        }

        using var scope = new AssertionScope();
        result.Should().Be("error: the file is busy; try again.");
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(timeout - TimeSpan.FromMilliseconds(50), "busy comes only after retrying for the whole contention timeout");
    }

    /// <summary>
    /// M-b's equivalent on both OSes: <c>sub</c>, with <c>inner.cs</c> in it, is renamed to <c>sub2</c> after the
    /// root was enumerated and right before the walk opens <c>sub</c>. The walk then found <c>sub</c> gone and skipped
    /// it, so the listing showed a <c>sub/</c> that no longer existed and missed <c>sub2/inner.cs</c>, a file that is
    /// in the workspace. The walk must notice that the tree changed under it and list it again as it now is.
    /// </summary>
    [Fact]
    public async Task A_subdirectory_renamed_during_the_walk_is_listed_where_it_now_is()
    {
        var (tools, _, root) = Build();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "inner.cs"), "x");
        File.WriteAllText(Path.Combine(root, "top.cs"), "x");
        var renamed = false;
        tools.BeforeOpenForTesting = candidate =>
        {
            if (!renamed && string.Equals(Path.GetFileName(candidate), "sub", StringComparison.Ordinal))
            {
                renamed = true;
                Directory.Move(Path.Combine(root, "sub"), Path.Combine(root, "sub2"));
            }
        };

        var result = await tools.ListFiles(Caller(RunId));

        using var scope = new AssertionScope();
        renamed.Should().BeTrue();
        result.Should().Be("sub2/\nsub2/inner.cs\ntop.cs");
    }

    private (WorkspaceTools Tools, FakeChangeListener Listener, string Root) Build(
        IReadOnlySet<string>? allowedWriteExtensions = null,
        IEnumerable<string>? protectedPaths = null,
        bool allowAnyWriteExtension = false,
        int? maxReadBytes = null,
        int? maxListEntries = null,
        TimeSpan? contentionTimeout = null,
        Func<string, IRunWorkspaceChangeListener>? extraListener = null)
    {
        var root = NewTempDir("thalos-workspace-tools-");
        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", root, null);

        var provider = new FakeRunWorkspaceProvider(workspace);

        var options = new RunWorkspaceToolOptions
        {
            AllowedWriteExtensions = allowedWriteExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".md" },
            AllowAnyWriteExtension = allowAnyWriteExtension,
        };
        if (maxReadBytes is { } bytes)
        {
            options.MaxReadBytes = bytes;
        }

        if (maxListEntries is { } entries)
        {
            options.MaxListEntries = entries;
        }

        if (contentionTimeout is { } timeout)
        {
            options.ContentionTimeout = timeout;
        }

        if (protectedPaths is not null)
        {
            foreach (var protectedPath in protectedPaths)
            {
                options.ProtectedPaths.Add(protectedPath);
            }
        }

        var fake = new FakeChangeListener();
        var extra = extraListener?.Invoke(root);
        IEnumerable<IRunWorkspaceChangeListener> registered = extra is null ? [fake] : [fake, extra];
        var tools = new WorkspaceTools(provider, options, registered, NullLogger<WorkspaceTools>.Instance);
        return (tools, fake, root);
    }

    /// <summary>Asserts that <paramref name="swap"/> fails with a sharing violation: an <see cref="IOException"/> whose HResult's low word is 32, <c>ERROR_SHARING_VIOLATION</c> (ruling (m)).</summary>
    private static void SwapIsRefusedWithASharingViolation(Action swap) =>
        (swap.Should().Throw<IOException>().Which.HResult & 0xFFFF).Should().Be(32);

    /// <summary>A second <see cref="WorkspaceTools"/> over an existing workspace root, with its own options and seams but the same process-wide lock and level tables, as the tool source's per-call instances have.</summary>
    private static WorkspaceTools ToolsOver(string root, IReadOnlySet<string>? allowedWriteExtensions = null)
    {
        var workspace = new RunWorkspace(RunId, "repo", "https://example.invalid/repo.git", "main", $"run/{RunId}", root, null);
        var options = new RunWorkspaceToolOptions
        {
            AllowedWriteExtensions = allowedWriteExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".md" },
        };
        return new WorkspaceTools(new FakeRunWorkspaceProvider(workspace), options, [new FakeChangeListener()], NullLogger<WorkspaceTools>.Instance);
    }

    /// <summary>Waits for a test's own sync point. Not an assertion: a sync point that is never reached is a broken test setup, reported as a <see cref="TimeoutException"/>.</summary>
    private static async Task SignalledOrThrowAsync(SemaphoreSlim signal, string failure)
    {
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException(failure);
        }
    }

    /// <summary>Opens <paramref name="directory"/> the way another process deleting it would: with <c>DELETE</c> access and a share mode without <c>FILE_SHARE_DELETE</c>, so every other open of it fails with a sharing violation while this handle stays open. Windows-only.</summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle HoldForDeletion(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A mandatory sharing mode exists only on Windows.");
        }

        var handle = PinnedIo.Windows.CreateFileW(directory, PinnedIo.Windows.Delete, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
        handle.IsInvalid.Should().BeFalse("the test's own holder must open");
        return handle;
    }

    private string NewTempDir(string prefix)
    {
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    private static TestSecurityContext Caller(Guid runId, string? writeExtensions = null)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = runId.ToString() };
        if (writeExtensions is not null)
        {
            claims[RunWorkspaceClaims.WriteExtensions] = writeExtensions;
        }

        return new TestSecurityContext("run-caller") { Claims = claims };
    }

    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (IOException ex)
        {
            LinkTestHelpers.FailOrSkip("create a file symlink", ex);
        }
    }

    private static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                LinkTestHelpers.FailOrSkip("create a directory junction", new InvalidOperationException(process.StandardError.ReadToEnd()));
            }

            return;
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LinkTestHelpers.FailOrSkip("create a directory link", ex);
        }
    }

    /// <summary>Answers <see cref="FindAsync"/> for one known run with <paramref name="workspace"/>; every other member is unused by these tests.</summary>
    private sealed class FakeRunWorkspaceProvider(RunWorkspace workspace) : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Not used by WorkspaceToolsTests.");

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) =>
            new(runId == workspace.RunId ? workspace : null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not used by WorkspaceToolsTests.");

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) =>
            throw new NotSupportedException("Not used by WorkspaceToolsTests.");
    }

    private sealed class FakeChangeListener : IRunWorkspaceChangeListener
    {
        public List<(Guid RunId, string Path)> Changes { get; } = [];

        public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths)
        {
            foreach (var path in relativePaths)
            {
                Changes.Add((runId, path));
            }
        }
    }

    private sealed class ThrowingChangeListener : IRunWorkspaceChangeListener
    {
        public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths) => throw new InvalidOperationException("listener boom");
    }

    /// <summary>Reads the changed file back through an ordinary, non-exclusive open, to prove the write's own handle is already closed by the time listeners run.</summary>
    private sealed class ReadingChangeListener(string root) : IRunWorkspaceChangeListener
    {
        public string? ReadBack { get; private set; }

        public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths) =>
            ReadBack = File.ReadAllText(Path.Combine(root, relativePaths[0]));
    }
}
