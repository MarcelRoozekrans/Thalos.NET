using System.Diagnostics;
using System.Text;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Tests for <see cref="GitCliRunWorkspaceGit"/> against a real worktree, made with A6's own
/// <see cref="LocalGitRemote"/> and a real <see cref="GitWorktreeWorkspaceProvider"/> — no fake transport, no
/// mocked git, exactly as <see cref="GitWorktreeWorkspaceProviderTests"/> tests A6 itself.
/// </summary>
public sealed class GitCliRunWorkspaceGitTests : IDisposable
{
    private static readonly GitAuthor TestAuthor = new("Test Author", "test-author@example.invalid");

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-run-git-").FullName;
    private readonly GitWorkspaceOptions _options;
    private readonly GitCliRunWorkspaceGit _git;
    private LocalGitRemote? _remote;

    public GitCliRunWorkspaceGitTests()
    {
        _options = new GitWorkspaceOptions { DataRoot = Path.Combine(_temp, "data") };
        _git = new GitCliRunWorkspaceGit(_options, NullLogger<GitCliRunWorkspaceGit>.Instance);
    }

    [Fact]
    public async Task An_excluded_path_stays_uncommitted_and_a_paths_commit_takes_only_it()
    {
        var ws = await WorktreeAsync(("AGENT.md", "Run dotnet test."));
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");

        var code = await _git.CommitAsync(ws, new GitCommitRequest { Message = "code", Author = TestAuthor, ExcludePaths = ["AGENT.md"] }, CancellationToken.None);
        var agent = await _git.CommitAsync(ws, new GitCommitRequest { Message = "agent", Author = TestAuthor, Paths = ["AGENT.md"] }, CancellationToken.None);

        code.Value.Created.Should().BeTrue();
        // Moved ahead of agent.Value.Created (fix round 1, ruling 5): this is the assertion the exclude/paths
        // scoping is actually about, so the red for a missing reset of ExcludePaths must land here, not on a
        // downstream side effect of the same bug.
        FilesIn(ws, code.Value.Sha).Should().Contain("code.cs").And.NotContain("AGENT.md");
        agent.Value.Created.Should().BeTrue();
        FilesIn(ws, agent.Value.Sha).Should().Equal("AGENT.md");
    }

    [Fact]
    public async Task A_repeated_commit_with_nothing_new_creates_nothing()
    {
        var ws = await WorktreeAsync();

        var result = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, ExcludePaths = ["AGENT.md"] }, CancellationToken.None);

        // Split (fix round 1, ruling 5): accessing .Value on a failed Result throws, which is not a red on the
        // named assertion below. Checking IsSuccess first turns a broken "diff --cached --quiet" check into a
        // clean assertion failure here instead.
        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeFalse();
    }

    [Fact]
    public async Task Diff_stat_lists_every_file_changed_since_the_merge_base_with_line_counts()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "a\nb\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "one", Author = TestAuthor }, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "c\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "two", Author = TestAuthor }, CancellationToken.None);

        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;
        stat.Should().BeEquivalentTo([new GitFileChange("code.cs", 2, 0), new GitFileChange("other.cs", 1, 0)]);
    }

    /// <summary>
    /// Minors: <c>--no-renames</c> means a rename shows up as a delete of the old path plus an add of the new one.
    /// The local <c>origin/main</c> tracking ref is advanced to the first commit before the rename, so
    /// <see cref="GitCliRunWorkspaceGit.DiffStatAsync"/>'s own merge-base diff isolates just the rename — otherwise
    /// the merge base would sit before <c>code.cs</c> ever existed, and its whole lifetime (add then rename) would
    /// collapse into a single "renamed.cs added" record with no delete record for <c>code.cs</c> to disable.
    /// </summary>
    [Fact]
    public async Task Diff_stat_reports_a_rename_as_a_delete_and_an_add_since_renames_are_disabled()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "a\nb\nc\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "one", Author = TestAuthor }, CancellationToken.None);
        Git(ws.Root, "update-ref refs/remotes/origin/main HEAD");
        File.Move(Path.Combine(ws.Root, "code.cs"), Path.Combine(ws.Root, "renamed.cs"));
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "two", Author = TestAuthor }, CancellationToken.None);

        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;

        stat.Should().BeEquivalentTo([new GitFileChange("code.cs", 0, 3), new GitFileChange("renamed.cs", 3, 0)]);
    }

    /// <summary>Minors: <c>-z</c> means a path with spaces, parentheses and non-ASCII characters parses correctly, unquoted.</summary>
    [Fact]
    public async Task Diff_stat_reports_a_path_containing_special_characters()
    {
        var ws = await WorktreeAsync();
        const string name = "a b (special) - 日本語.cs";
        File.WriteAllText(Path.Combine(ws.Root, name), "x\n");

        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;

        stat.Should().BeEquivalentTo([new GitFileChange(name, 1, 0)]);
    }

    [Fact]
    public async Task Push_sends_the_run_branch_to_its_remote_and_a_repeat_changes_nothing()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        var sha = (await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None)).Value.Sha;

        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();
        // TryHeadOf, not HeadOf (fix round 1, ruling 5): a wrong-branch push leaves ws.Branch entirely absent on
        // the remote, and HeadOf throws on that; TryHeadOf returns null, landing the red on Should().Be(sha).
        _remote!.TryHeadOf(ws.Branch).Should().Be(sha);
        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _remote!.TryHeadOf(ws.Branch).Should().Be(sha);
    }

    /// <summary>Push scope ruling: a run's branch equal to the repository's default branch is refused, and the remote's default branch is never touched.</summary>
    [Fact]
    public async Task Pushing_a_branch_equal_to_the_default_branch_is_refused_and_the_remote_is_unchanged()
    {
        var ws = await WorktreeAsync();
        // The mirror's initial bare clone already holds a real local refs/heads/<DefaultBranch> (a bare clone
        // copies every branch directly, before CloneMirrorAsync reconfigures future fetches under refs/remotes/
        // origin/*), so checking it out here makes HEAD really point at refs/heads/<DefaultBranch> — isolating
        // this refusal from the separate symbolic-ref check below, which a workspace merely relabelled via "with"
        // would trip on its own.
        Git(ws.Root, $"checkout {ws.DefaultBranch}");
        var mainBefore = _remote!.TryHeadOf(ws.DefaultBranch);

        // Committed on main itself, after checkout, not on the run's own branch: a push that fast-forwarded main
        // despite the refusal would move it past mainBefore, so this genuinely falsifies "the remote is unchanged"
        // (fix round 2 minor — committing before the checkout left main identical to origin/main, so even a
        // broken refusal's push would have changed nothing and this assertion could never go red).
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        var pushed = await _git.PushAsync(ws with { Branch = ws.DefaultBranch }, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        _remote!.TryHeadOf(ws.DefaultBranch).Should().Be(mainBefore);
    }

    /// <summary>Push scope ruling: the push argv is exactly the explicit two-sided refspec, with no <c>--force</c> and no <c>--tags</c>.</summary>
    [Fact]
    public async Task Push_argv_holds_exactly_the_explicit_refspec_with_no_force_and_no_tags()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        var captureFile = Path.Combine(_temp, "push-argv-capture.log");
        File.WriteAllText(captureFile, string.Empty);
        var spyGit = WriteSpyGit(_temp, captureFile);
        var spyOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = spyGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(spyOptions, NullLogger<GitCliRunWorkspaceGit>.Instance);

        (await git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var pushLine = File.ReadAllLines(captureFile).Single(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("push", StringComparer.Ordinal));
        var refspec = $"refs/heads/{ws.Branch}:refs/heads/{ws.Branch}";
        var tokens = pushLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        tokens.Should().Contain(refspec).And.Contain(ws.Remote);
        // Token-based, not substring: a `+`-prefixed refspec forces the push on the wire the same as --force does,
        // and -f is --force's short form (fix round 2 minor).
        tokens.Should().NotContain("--force");
        tokens.Should().NotContain("-f");
        tokens.Should().NotContain("--tags");
        tokens.Should().NotContain($"+{refspec}");
    }

    /// <summary>Push scope ruling: pushing straight to the workspace's URL writes nothing into the mirror's shared config.</summary>
    [Fact]
    public async Task The_mirrors_config_is_unchanged_after_a_push()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        var configPath = Path.Combine(MirrorOf(), "config");
        var configBefore = File.ReadAllText(configPath);

        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();

        File.ReadAllText(configPath).Should().Be(configBefore);
    }

    [Fact]
    public async Task Push_asks_the_credential_source_for_the_runs_remote_and_never_stores_the_header()
    {
        var source = new RecordingCredentialSource(new GitCredentials("x-access-token", "secret-value"));
        var git = new GitCliRunWorkspaceGit(_options, NullLogger<GitCliRunWorkspaceGit>.Instance, source);
        var ws = await WorktreeAsync();

        await git.PushAsync(ws, CancellationToken.None); // a local remote ignores the header; the push still succeeds

        source.RequestedUrls.Should().Equal(ws.Remote);
        Git(ws.Root, "config --get-all http.extraHeader").Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_to_a_missing_remote_fails_naming_git_and_not_the_secret()
    {
        var git = new GitCliRunWorkspaceGit(_options, NullLogger<GitCliRunWorkspaceGit>.Instance, new RecordingCredentialSource(new GitCredentials("x-access-token", "secret-value")));
        var ws = await WorktreeAsync();
        _remote!.Delete();

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        pushed.Error.ToString().Should().NotContain("secret-value").And.NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:secret-value")));
    }

    /// <summary>
    /// Fix round 1, item 5: a git that actually echoes the secret env value to stderr (rather than a real, local
    /// file remote that never mentions it at all) is the only way to falsify the <c>Scrub</c> call itself.
    /// </summary>
    [Fact]
    public async Task Push_scrubs_the_credential_header_out_of_an_error_that_echoes_it()
    {
        var ws = await WorktreeAsync();
        var echoingGit = WriteEchoingGit(_temp);
        const string token = "scrub-red-token-value";
        var echoOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = echoingGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(echoOptions, NullLogger<GitCliRunWorkspaceGit>.Instance, new RecordingCredentialSource(new GitCredentials("x-access-token", token)));

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        var expectedHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        pushed.Error.ToString().Should().NotContain(expectedHeader).And.NotContain(token);
    }

    /// <summary>Commit scope ruling: a failed commit attempt must never leave files staged for the next commit to pick up.</summary>
    [Fact]
    public async Task A_failed_commit_does_not_leak_staged_files_into_the_next_commit()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "leftover");
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");

        // git refuses an empty commit message, so this stages everything (via add -A) but never commits it.
        var failed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "", Author = TestAuthor }, CancellationToken.None);
        failed.IsFailure.Should().BeTrue();

        var agent = await _git.CommitAsync(ws, new GitCommitRequest { Message = "agent", Author = TestAuthor, Paths = ["AGENT.md"] }, CancellationToken.None);

        agent.Value.Created.Should().BeTrue();
        FilesIn(ws, agent.Value.Sha).Should().Equal("AGENT.md");
    }

    /// <summary>
    /// Commit scope ruling: <c>--literal-pathspecs</c> means <c>Paths</c> is matched by its exact name, never as a
    /// glob. <c>"AGENT.m[d]"</c> is a git glob (a character class matching the single character <c>d</c>) that
    /// would match <c>AGENT.md</c> under git's default pathspec magic — the exact shape of the glob bug — but is
    /// not itself a real file, and its brackets are valid on every filesystem this suite runs on (unlike <c>*</c> or
    /// <c>?</c>, both reserved on Windows, which would make <see cref="WorkspacePath.Resolve"/> refuse the path for
    /// an unrelated reason before git is ever involved). Taken literally, <c>"AGENT.m[d]"</c> is an absent,
    /// untracked path, so it contributes nothing: no commit is made — never silently staging (and so committing)
    /// <c>AGENT.md</c>, and never widening to every file. The presence check itself (<c>git ls-files</c>) runs with
    /// <c>--literal-pathspecs</c> too, so the glob cannot sneak <c>AGENT.md</c> in through it either.
    /// </summary>
    [Fact]
    public async Task A_glob_looking_path_is_taken_literally_and_never_matches_a_real_file()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");
        var headBefore = Git(ws.Root, "rev-parse HEAD");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["AGENT.m[d]"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue("'AGENT.m[d]' taken literally is an absent, untracked path, which is not an error");
        committed.Value.Created.Should().BeFalse("'AGENT.m[d]' must be taken as a literal, nonexistent filename, never as a glob matching AGENT.md");
        Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore, "no commit must be made when the literal path matches nothing");
        Git(ws.Root, "diff --name-only").Should().Be("AGENT.md", "AGENT.md's change stays unstaged in the worktree");
    }

    /// <summary>
    /// Commit scope ruling: <c>--literal-pathspecs</c> means <c>ExcludePaths</c> is matched by its exact name too.
    /// Unlike <c>git add</c>, <c>git reset</c> does not error on a pathspec that matches nothing — it simply
    /// excludes nothing — so with the glob bug fixed, <c>AGENT.md</c>'s own real change survives and is committed,
    /// where the bug would have glob-matched <c>"AGENT.m[d]"</c> against <c>AGENT.md</c> and silently dropped it.
    /// </summary>
    [Fact]
    public async Task A_glob_looking_exclude_path_is_taken_literally_and_does_not_exclude_a_real_file()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, ExcludePaths = ["AGENT.m[d]"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Created.Should().BeTrue("'AGENT.m[d]' must be taken as a literal, nonexistent filename, never as a glob that excludes AGENT.md");
        FilesIn(ws, committed.Value.Sha).Should().Contain("AGENT.md");
    }

    /// <summary>
    /// Absent-path contract: a <c>Paths</c> entry that is neither on disk nor tracked contributes nothing — it is
    /// not an error. With nothing else listed, nothing is staged, so no commit is made and <c>Sha</c> is
    /// <c>HEAD</c>'s own, unchanged. This is the Daedalus shape: a path-scoped commit of a standing-instructions
    /// file the repository does not have.
    /// </summary>
    [Fact]
    public async Task An_absent_untracked_path_commits_nothing_and_head_is_unchanged()
    {
        var ws = await WorktreeAsync();
        var headBefore = Git(ws.Root, "rev-parse HEAD");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["STANDING.md"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue("a path that is neither on disk nor tracked contributes nothing, and is not an error");
        committed.Value.Created.Should().BeFalse();
        committed.Value.Sha.Should().Be(headBefore);
        Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore);
    }

    /// <summary>Absent-path contract: an absent, untracked entry beside a present one stages only the present one, and nothing outside the list.</summary>
    [Fact]
    public async Task An_absent_path_beside_a_present_one_stages_only_the_present_one()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "class O {}");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["STANDING.md", "code.cs"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Created.Should().BeTrue();
        FilesIn(ws, committed.Value.Sha).Should().Equal("code.cs");
    }

    /// <summary>Absent-path contract: a tracked path deleted from disk is not "absent" — its deletion is still staged and committed.</summary>
    [Fact]
    public async Task An_absent_path_beside_a_tracked_deleted_one_commits_the_deletion()
    {
        var ws = await WorktreeAsync(("old.txt", "old"));
        File.Delete(Path.Combine(ws.Root, "old.txt"));
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "class O {}");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["STANDING.md", "old.txt"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Created.Should().BeTrue();
        // Only the deletion is committed; other.cs, outside the list, is not.
        FilesIn(ws, committed.Value.Sha).Should().Equal("old.txt");
    }

    /// <summary>
    /// Stage-everything guard: when every <c>Paths</c> entry is absent and untracked, the filtered list is empty,
    /// and an empty list must never fall through to <c>git add -A</c> with no pathspec — that would stage, and
    /// commit, every other change in the worktree. Nothing is committed, and the other changes stay exactly as they
    /// were: modified-but-unstaged and untracked.
    /// </summary>
    [Fact]
    public async Task Every_path_absent_commits_nothing_and_never_stages_the_rest_of_the_worktree()
    {
        var ws = await WorktreeAsync(("tracked.txt", "before"));
        File.WriteAllText(Path.Combine(ws.Root, "tracked.txt"), "after");
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "class O {}");
        var headBefore = Git(ws.Root, "rev-parse HEAD");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["STANDING.md", "missing/NOTES.md"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Created.Should().BeFalse("an all-absent path list must never widen to staging the whole worktree");
        Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore);
        Git(ws.Root, "diff --name-only").Should().Be("tracked.txt", "the tracked change stays in the worktree, unstaged and uncommitted");
        Git(ws.Root, "ls-files --others --exclude-standard").Should().Be("other.cs", "the untracked file stays untracked");
    }

    /// <summary>
    /// Empty-list contract: only <see langword="null"/> <c>Paths</c> stages everything. An explicit empty list names
    /// nothing, so nothing is staged and no commit is made, however much else in the worktree has changed — the same
    /// guard as a list every entry of which is absent.
    /// </summary>
    [Fact]
    public async Task An_explicit_empty_path_list_commits_nothing_and_never_stages_the_rest_of_the_worktree()
    {
        var ws = await WorktreeAsync(("tracked.txt", "before"));
        File.WriteAllText(Path.Combine(ws.Root, "tracked.txt"), "after");
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "class O {}");
        var headBefore = Git(ws.Root, "rev-parse HEAD");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = [] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Created.Should().BeFalse("an explicit empty path list names nothing; only null stages everything");
        committed.Value.Sha.Should().Be(headBefore);
        Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore);
        Git(ws.Root, "diff --name-only").Should().Be("tracked.txt", "the tracked change stays in the worktree, unstaged and uncommitted");
        Git(ws.Root, "ls-files --others --exclude-standard").Should().Be("other.cs", "the untracked file stays untracked");
    }

    /// <summary>
    /// Absent-path contract for <c>ExcludePaths</c>: <c>git reset -q -- &lt;path&gt;</c> exits 0 on a pathspec
    /// that matches nothing (verified against git 2.54), so an absent, untracked exclusion excludes nothing and the
    /// commit goes ahead. Pinned so a later change to how exclusions are unstaged cannot quietly make it an error.
    /// </summary>
    [Fact]
    public async Task An_absent_untracked_exclude_path_is_not_an_error()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, ExcludePaths = ["STANDING.md"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue("an absent, untracked exclusion excludes nothing and is not an error");
        committed.Value.Created.Should().BeTrue();
    }

    /// <summary>
    /// A real git failure still fails the commit. This pins the reset-to-<c>HEAD</c> failure: with <c>index.lock</c>
    /// present, the initial <c>git reset -q</c> exits 128, so the call fails before it ever reaches the path filter or
    /// <c>git add</c>. The ignored-path test below covers a failing <c>git add</c> itself.
    /// </summary>
    [Fact]
    public async Task A_locked_index_still_fails_the_commit()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var lockFile = Path.GetFullPath(Path.Combine(ws.Root, Git(ws.Root, "rev-parse --git-path index.lock")));
        File.WriteAllText(lockFile, "");

        try
        {
            var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["code.cs"] }, CancellationToken.None);

            committed.IsFailure.Should().BeTrue("a locked index makes the reset to HEAD fail, a real git failure");
        }
        finally
        {
            File.Delete(lockFile);
        }
    }

    /// <summary>
    /// Only the unmatched-pathspec case is removed: a present path that <c>git add</c> itself refuses — an ignored
    /// file — still fails the commit, exactly as before, even beside an absent path. Presence is decided from disk
    /// and the index, never from git's error text, so this refusal is never mistaken for an absent path.
    /// </summary>
    [Fact]
    public async Task An_ignored_present_path_beside_an_absent_one_still_fails()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, ".gitignore"), "ignored.log\n");
        File.WriteAllText(Path.Combine(ws.Root, "ignored.log"), "noise");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["STANDING.md", "ignored.log"] }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("git add refusing an ignored present path is a real failure, not an absent path");
    }

    /// <summary>
    /// The 0.14.1 sandbox publish defect, reproduced with real git. A patch, built the way a sandbox builds one, adds a
    /// <c>.gitignore</c> matching <c>*.gen</c>, adds <c>out.gen</c> (staged with <c>-f</c> in the agent's tree), makes
    /// <c>run.sh</c> executable, modifies <c>a.txt</c> and deletes <c>gone.txt</c>. It is applied with
    /// <c>git apply --index</c>, as <see cref="GitPatchApplier"/> does, in a worktree with <c>core.fileMode=false</c>,
    /// the Windows default, set locally. The host then writes its own <c>STANDING.md</c>, stages it, and commits with
    /// <see cref="GitCommitRequest.CommitStagedIndex"/>, excluding it. A reset plus <c>add -A</c> would drop
    /// <c>out.gen</c>, which the new <c>.gitignore</c> matches, and lose <c>run.sh</c>'s mode, which
    /// <c>core.fileMode=false</c> keeps git from reading off disk.
    /// </summary>
    /// <remarks>
    /// Setup guard, the applied index holds the mode change: Red: drop the <c>update-index --chmod=+x</c> from the patch.
    /// Setup guard, the applied index holds out.gen: Red: drop the <c>add -f out.gen</c> from the patch.
    /// The commit succeeds: Red: make <c>ValidateRequest</c> refuse CommitStagedIndex unconditionally.
    /// The excluded host file is not in the commit: Red: drop the ExcludePaths unstaging from the CommitStagedIndex path
    /// of <c>StageAsync</c>.
    /// The commit's tree is the applier's index tree: Red: route CommitStagedIndex through the old reset-plus-add path,
    /// <c>if (!request.CommitStagedIndex)</c> made <c>if (true)</c> in <c>StageAsync</c>; the tree lacks out.gen and
    /// run.sh's mode.
    /// </remarks>
    [Fact]
    public async Task A_staged_index_commit_publishes_exactly_the_index_git_apply_staged()
    {
        var ws = await WorktreeAsync(("run.sh", "#!/bin/sh\necho run\n"), ("a.txt", "a\n"), ("gone.txt", "gone\n"));
        var patch = BuildPatch(ws, dir =>
        {
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "*.gen\n");
            File.WriteAllText(Path.Combine(dir, "out.gen"), "generated\n");
            File.AppendAllText(Path.Combine(dir, "a.txt"), "more\n");
            File.Delete(Path.Combine(dir, "gone.txt"));
            LocalGitRemote.RunGit(dir, "add", "-A");
            LocalGitRemote.RunGit(dir, "add", "-f", "out.gen");
            LocalGitRemote.RunGit(dir, "update-index", "--chmod=+x", "run.sh");
        });
        LocalGitRemote.RunGit(ws.Root, "config", "core.fileMode", "false");
        LocalGitRemote.RunGit(ws.Root, "-c", "core.autocrlf=false", "apply", "--index", "--binary", patch);
        var appliedTree = LocalGitRemote.RunGit(ws.Root, "write-tree");
        LocalGitRemote.RunGit(ws.Root, "ls-files", "-s", "run.sh").Should().StartWith("100755", "the patch's mode change must be in the applied index");
        LocalGitRemote.RunGit(ws.Root, "ls-files", "out.gen").Should().Be("out.gen", "the patch's ignored file must be in the applied index");
        File.WriteAllText(Path.Combine(ws.Root, "STANDING.md"), "host-written\n");
        LocalGitRemote.RunGit(ws.Root, "add", "STANDING.md");

        var committed = await _git.CommitAsync(
            ws,
            new GitCommitRequest { Message = "run", Author = TestAuthor, CommitStagedIndex = true, ExcludePaths = ["STANDING.md"] },
            CancellationToken.None);

        committed.IsSuccess.Should().BeTrue(committed.IsFailure ? committed.Error.Message + " " + committed.Error.Detail : "");
        using var _ = new AssertionScope();
        FilesIn(ws, committed.Value.Sha).Should().NotContain("STANDING.md");
        var committedTree = LocalGitRemote.RunGit(ws.Root, "rev-parse", committed.Value.Sha + "^{tree}");
        // The diagnostic diff forces core.fileMode=true: under this worktree's false, git diff hides a mode-only change
        // even between two trees, and the lost mode would be missing from the message.
        committedTree.Should().Be(
            appliedTree,
            "the commit must be the index the applier checked; they differ by: " + LocalGitRemote.RunGit(ws.Root, "-c", "core.fileMode=true", "diff", "--raw", "--no-renames", appliedTree, committedTree));
    }

    /// <summary>
    /// <see cref="GitCommitRequest.CommitStagedIndex"/> stages nothing from disk: with an empty index and a changed
    /// worktree, nothing is committed, the existing "nothing to commit" outcome of <c>Created</c> false and
    /// <c>HEAD</c>'s unchanged sha, and the changes stay unstaged.
    /// </summary>
    /// <remarks>
    /// The call succeeds: Red: make <c>ValidateRequest</c> refuse CommitStagedIndex unconditionally.
    /// Every assertion in the scope after it: Red: route CommitStagedIndex through the old reset-plus-add path;
    /// <c>add -A</c> stages and commits tracked.txt and other.cs. That change leaves the success line green.
    /// </remarks>
    [Fact]
    public async Task A_staged_index_commit_with_nothing_staged_commits_nothing_and_stages_nothing()
    {
        var ws = await WorktreeAsync(("tracked.txt", "before"));
        File.WriteAllText(Path.Combine(ws.Root, "tracked.txt"), "after");
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "class O {}");
        var headBefore = Git(ws.Root, "rev-parse HEAD");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, CommitStagedIndex = true }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        using var _ = new AssertionScope();
        committed.Value.Created.Should().BeFalse("nothing was staged, and CommitStagedIndex stages nothing from disk");
        committed.Value.Sha.Should().Be(headBefore);
        Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore);
        Git(ws.Root, "diff --name-only").Should().Be("tracked.txt", "the tracked change stays unstaged");
        Git(ws.Root, "ls-files --others --exclude-standard").Should().Be("other.cs", "the untracked file stays untracked");
    }

    /// <summary>
    /// <see cref="GitCommitRequest.CommitStagedIndex"/> with a <see cref="GitCommitRequest.Paths"/> list, an empty one
    /// included, is refused as a validation failure before anything is staged: the index and <c>HEAD</c> are untouched.
    /// </summary>
    /// <remarks>
    /// The failure, <c>HEAD</c> and the index, in one scope: Red: drop the CommitStagedIndex-with-Paths check from
    /// <c>ValidateRequest</c>; the call then commits the staged index and succeeds, moving <c>HEAD</c>, and the index no
    /// longer differs from it.
    /// The error code: Red: return <c>AgentError.GitOperationFailed</c> for the refusal instead of a validation error.
    /// The message: Red: reword the refusal so it names neither the option nor Paths.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_staged_index_commit_with_a_path_list_is_refused_and_changes_nothing(bool empty)
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        Git(ws.Root, "add code.cs");
        var headBefore = Git(ws.Root, "rev-parse HEAD");
        IReadOnlyList<string> paths = empty ? [] : ["code.cs"];

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, CommitStagedIndex = true, Paths = paths }, CancellationToken.None);

        using (new AssertionScope())
        {
            committed.IsFailure.Should().BeTrue();
            Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore);
            Git(ws.Root, "diff --cached --name-only").Should().Be("code.cs", "the staged index is left exactly as it was");
        }

        committed.Error.Code.Should().Be(AgentErrorCode.Validation);
        committed.Error.Message.Should().Contain("CommitStagedIndex").And.Contain("Paths");
    }

    /// <summary>
    /// Fix round 1, item 6, updated for fix round 3 item 1: <c>core.hooksPath</c> is not on
    /// <see cref="MirrorConfigSurface"/>'s allow-list, so <see cref="GitCliRunWorkspaceGit.CommitAsync"/>'s own
    /// config-surface check (added by round 3) now refuses a mirror configuring it before any git call — including
    /// the <c>add</c>/<c>reset</c>/<c>diff</c>/<c>commit</c> sequence <c>-c core.hooksPath=...</c> itself used to be
    /// the only defence against. The hooks plainly never fire, since nothing runs, and the same competing hooks
    /// directory and default hooks directory setup as before covers both surfaces.
    /// </summary>
    [Fact]
    public async Task A_competing_hooksPath_in_the_mirrors_config_refuses_the_commit_and_no_hook_ever_fires()
    {
        var ws = await WorktreeAsync();
        var mirror = MirrorOf();
        var competingHooksDir = Path.Combine(_temp, "competing-hooks");
        Directory.CreateDirectory(competingHooksDir);
        LocalGitRemote.RunGit(mirror, "config", "core.hooksPath", competingHooksDir.Replace('\\', '/'));

        var hooks = new (string Name, string Marker)[]
        {
            ("pre-commit", Path.Combine(_temp, "pre-commit.marker")),
            ("commit-msg", Path.Combine(_temp, "commit-msg.marker")),
            ("post-commit", Path.Combine(_temp, "post-commit.marker")),
        };
        var defaultHooksDir = Path.Combine(mirror, "hooks");
        Directory.CreateDirectory(defaultHooksDir);
        foreach (var (name, marker) in hooks)
        {
            WriteHookScript(competingHooksDir, name, marker);
            WriteHookScript(defaultHooksDir, name, marker);
        }

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("core.hooksPath is outside MirrorConfigSurface's allow-list, so CommitAsync must refuse before any git call runs");
        foreach (var (_, marker) in hooks)
        {
            File.Exists(marker).Should().BeFalse($"'{marker}' must never be created: the commit must be refused before git ever runs, let alone a hook");
        }
    }

    /// <summary>
    /// Decisions ruling 2: the credential header must never reach argv on a push, verified here with a "spy" git
    /// wrapper that logs every argv it receives and then execs the real git, so the push still succeeds normally —
    /// the same technique A6's own <c>Credentials_never_appear_on_the_git_command_line</c> test uses for a clone.
    /// </summary>
    [Fact]
    public async Task Push_never_puts_the_credential_header_on_the_git_command_line()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        var captureFile = Path.Combine(_temp, "argv-capture.log");
        File.WriteAllText(captureFile, string.Empty);
        var spyGit = WriteSpyGit(_temp, captureFile);
        const string token = "super-secret-push-token-value";
        var spyOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = spyGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(spyOptions, NullLogger<GitCliRunWorkspaceGit>.Instance, new RecordingCredentialSource(new GitCredentials("x-access-token", token)));

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsSuccess.Should().BeTrue();
        var captured = File.ReadAllText(captureFile);
        captured.Should().NotContain(token, "the credential value must never reach argv");
        captured.Should().NotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}")),
            "the encoded credential header must never reach argv either");
    }

    /// <summary>
    /// Fix round 2, item 4 minor, updated for fix round 3 item 1: <c>core.fsmonitor</c> is not on
    /// <see cref="MirrorConfigSurface"/>'s allow-list, so <see cref="GitCliRunWorkspaceGit.CommitAsync"/>'s own
    /// config-surface check (added by round 3) now refuses a mirror configuring it before any git call runs —
    /// superseding round 2's framing, which planted it after create specifically because a create-time gate would
    /// otherwise have refused it and <see cref="CommitAsync"/> had no gate of its own yet.
    /// </summary>
    [Fact]
    public async Task An_fsmonitor_in_the_mirrors_config_refuses_the_commit_and_it_never_fires()
    {
        var ws = await WorktreeAsync();
        var marker = Path.Combine(_temp, "fsmonitor-ran.marker");
        var fsmonitorScript = WriteFsmonitorScript(_temp, marker);
        LocalGitRemote.RunGit(MirrorOf(), "config", "core.fsmonitor", fsmonitorScript.Replace('\\', '/'));

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("core.fsmonitor is outside MirrorConfigSurface's allow-list, so CommitAsync must refuse before any git call runs");
        File.Exists(marker).Should().BeFalse("the commit must be refused before git ever runs, let alone queries an fsmonitor hook");
    }

    /// <summary>
    /// Fix round 2, item 3: checked in <see cref="GitCliRunWorkspaceGit.PushAsync"/> itself, not only at create,
    /// because the mirror's config could be tampered with in the interval between a successful create and a later
    /// push. Uses a spy git to prove the refusal happens before <c>git push</c> is ever invoked — no network call.
    /// </summary>
    [Fact]
    public async Task Push_refuses_when_the_mirrors_config_defines_a_push_redirect_before_any_network_call()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        LocalGitRemote.RunGit(MirrorOf(), "config", "url.https://evil.example/redirect.git.pushInsteadOf", ws.Remote);

        var captureFile = Path.Combine(_temp, "pushinsteadof-argv.log");
        File.WriteAllText(captureFile, string.Empty);
        var spyGit = WriteSpyGit(_temp, captureFile);
        var spyOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = spyGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(spyOptions, NullLogger<GitCliRunWorkspaceGit>.Instance);

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        File.ReadAllLines(captureFile).Should().NotContain(
            l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("push", StringComparer.Ordinal),
            "the mirror's disallowed config must refuse the push before any network-capable git call runs");
    }

    /// <summary>
    /// Fix round 2, item 1 (CRITICAL, pre-existing): a review probe found that, without
    /// <c>http.followRedirects=false</c>, an origin answering with an HTTP redirect makes git re-target the
    /// request — and, for the push's own POST, resend the Authorization header — to whatever host the redirect
    /// names. With the pin, git refuses to follow any redirect at all, so the redirect's target never receives a
    /// request in the first place, header or not — a strictly stronger guarantee than "no header", and simpler to
    /// prove without reimplementing git's smart-HTTP protocol in a fake server.
    /// </summary>
    /// <remarks>
    /// Drives a raw <see cref="GitCli"/> call directly, with <c>protocol.http.allow=always</c> passed as this
    /// call's own <c>extraConfig</c> (which overrides the isolation pin, added earlier on the same command line —
    /// git's own last-one-wins rule for repeated <c>-c</c> keys), rather than going through
    /// <see cref="GitCliRunWorkspaceGit.PushAsync"/> as every other push test does. Plain HTTP is otherwise refused
    /// outright by the separate <c>protocol.http.allow=never</c> pin (covered on its own by
    /// <see cref="GitCli_still_refuses_http_even_when_the_repository_config_allows_it"/>), which would make this
    /// test fail for the wrong reason — never even attempting the origin at all — rather than exercising
    /// <c>http.followRedirects</c> specifically.
    /// </remarks>
    [Fact]
    public async Task A_redirecting_origin_never_gets_git_to_contact_the_redirect_target()
    {
        using var origin = new RedirectingHttpServer();
        using var target = new RecordingHttpServer();
        origin.RedirectTo(target.Prefix);

        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        var git = new GitCli(_options);

        var pushed = await git.RunAsync(
            ws.Root,
            ["push", "--end-of-options", origin.Prefix + "r.git", $"refs/heads/{ws.Branch}:refs/heads/{ws.Branch}"],
            ["protocol.http.allow=always"],
            null,
            CancellationToken.None);

        pushed.Succeeded.Should().BeFalse();
        target.RequestCount.Should().Be(0, "http.followRedirects=false must stop git from ever following the origin's redirect to another host");
    }

    /// <summary>
    /// Fix round 2, item 2: verifies <see cref="GitCli"/>'s own command-line protocol pins directly, independent of
    /// <see cref="GitWorktreeWorkspaceProvider"/>'s config allow-list (item 3), which would otherwise refuse this
    /// scratch repository's mirror outright at create time and make this path unreachable through a normal create.
    /// </summary>
    [Fact]
    public async Task GitCli_still_refuses_http_even_when_the_repository_config_allows_it()
    {
        var scratch = Path.Combine(_temp, "protocol-scratch");
        LocalGitRemote.RunGit(_temp, "init", "-q", "--bare", scratch);
        LocalGitRemote.RunGit(scratch, "config", "protocol.http.allow", "always");
        var git = new GitCli(_options);

        var result = await git.RunAsync(scratch, ["fetch", "http://127.0.0.1:1/definitely-not-a-real-remote.git"], null, null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StdErr.Should().Contain("not allowed", "the fetch must be refused for the protocol itself, not merely fail to connect");
    }

    /// <summary>Writes a script <c>core.fsmonitor</c> can point at, which touches <paramref name="markerPath"/> if git ever invokes it.</summary>
    private static string WriteFsmonitorScript(string dir, string markerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "fsmonitor-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path, "@echo off\r\n" + $"echo. >> \"{markerPath}\"\r\n" + "exit /b 1\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "fsmonitor-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\n" + $"touch \"{markerPath}\"\n" + "exit 1\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>An HTTP server that answers every request with a 302 to a fixed prefix, for <see cref="A_redirecting_origin_never_gets_git_to_contact_the_redirect_target"/>.</summary>
    private sealed class RedirectingHttpServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener;
        private string _redirectTo = "";

        public string Prefix { get; }

        public RedirectingHttpServer()
        {
            Prefix = $"http://127.0.0.1:{GetFreePort()}/";
            _listener = new System.Net.HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = ServeAsync();
        }

        public void RedirectTo(string prefix) => _redirectTo = prefix;

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException)
                {
                    // The listener was stopped, most likely by Dispose while this call was pending.
                    return;
                }

                try
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = _redirectTo.TrimEnd('/') + ctx.Request.Url!.AbsolutePath;
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or IOException)
                {
                    // Best effort per request — e.g. the client (git) already gave up on the connection; keep
                    // serving for whatever request comes next.
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException)
            {
                // Best effort: already stopped is not a reason to fail test teardown.
            }
        }
    }

    /// <summary>An HTTP server that counts every request it receives, for <see cref="A_redirecting_origin_never_gets_git_to_contact_the_redirect_target"/>.</summary>
    private sealed class RecordingHttpServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener;
        private int _requestCount;

        public string Prefix { get; }

        public int RequestCount => _requestCount;

        public RecordingHttpServer()
        {
            Prefix = $"http://127.0.0.1:{GetFreePort()}/";
            _listener = new System.Net.HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException)
                {
                    // The listener was stopped, most likely by Dispose while this call was pending.
                    return;
                }

                Interlocked.Increment(ref _requestCount);
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or IOException)
                {
                    // Best effort — the request is already counted above regardless of whether the response write
                    // succeeds.
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException)
            {
                // Best effort: already stopped is not a reason to fail test teardown.
            }
        }
    }

    /// <summary>
    /// Fix round 3, item 1 (IMPORTANT): a review probe added <c>filter.x.clean</c> to the mirror's config and
    /// <c>*.cs filter=x</c> to its <c>info/attributes</c> after a successful create, then found
    /// <see cref="GitCliRunWorkspaceGit.CommitAsync"/> ran the filter — command execution on <c>add</c>. The
    /// filter's clean command touches a marker file; a successful attack makes the marker exist.
    /// </summary>
    [Fact]
    public async Task A_filter_driver_in_the_mirrors_config_refuses_the_commit_and_never_runs()
    {
        var ws = await WorktreeAsync();
        var mirror = MirrorOf();
        var marker = Path.Combine(_temp, "filter-ran.marker").Replace('\\', '/');
        LocalGitRemote.RunGit(mirror, "config", "filter.x.clean", $"sh -c 'touch \"{marker}\"; cat'");
        Directory.CreateDirectory(Path.Combine(mirror, "info"));
        File.AppendAllText(Path.Combine(mirror, "info", "attributes"), "*.cs filter=x\n");

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("filter.x.clean is outside MirrorConfigSurface's allow-list, so CommitAsync must refuse before staging ever runs the filter");
        File.Exists(marker).Should().BeFalse("the filter's clean command must never execute");
    }

    /// <summary>
    /// Fix round 3, item 2 (minor): git treats a config subsection as case-sensitive, so <c>remote.ORIGIN.url</c>
    /// names a different remote to git than <c>remote.origin.url</c> — a key this provider never wrote, and one
    /// the allow-list must refuse, not silently accept as if it were the same key under a case-insensitive
    /// comparison.
    /// </summary>
    [Fact]
    public async Task A_differently_cased_remote_subsection_is_refused()
    {
        var ws = await WorktreeAsync();
        LocalGitRemote.RunGit(MirrorOf(), "config", "remote.ORIGIN.url", "https://evil.example/redirect.git");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("remote.ORIGIN.url is a different config key to git than remote.origin.url, and is not itself allow-listed");
    }

    /// <summary>Fix round 3, item 3: <c>core.symlinks</c> must hold exactly <c>false</c>, the value the provider itself writes.</summary>
    [Fact]
    public async Task A_changed_core_symlinks_value_is_refused()
    {
        var ws = await WorktreeAsync();
        LocalGitRemote.RunGit(MirrorOf(), "config", "core.symlinks", "true");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("core.symlinks must hold exactly the value the provider wrote, 'false'");
    }

    /// <summary>Fix round 3, item 3: <c>remote.origin.fetch</c> must appear exactly once.</summary>
    [Fact]
    public async Task A_second_remote_origin_fetch_value_is_refused()
    {
        var ws = await WorktreeAsync();
        LocalGitRemote.RunGit(MirrorOf(), "config", "--add", "remote.origin.fetch", "+refs/heads/other:refs/remotes/origin/other");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("remote.origin.fetch must be set exactly once");
    }

    /// <summary>
    /// Fix round 3, item 3: a single-value change to <c>remote.origin.url</c> after this workspace's own create
    /// refuses a later push through it — <see cref="GitCliRunWorkspaceGit.PushAsync"/>'s own check compares
    /// against this workspace's own <see cref="RunWorkspace.Remote"/>, so it never silently pushes once the
    /// mirror's shared config no longer agrees with this workspace's own record of where it belongs.
    /// </summary>
    [Fact]
    public async Task A_changed_remote_origin_url_refuses_a_push()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        LocalGitRemote.RunGit(MirrorOf(), "config", "remote.origin.url", "https://evil.example/redirect.git");

        var pushed = await _git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue("remote.origin.url must equal this workspace's own configured remote");
    }

    /// <summary>
    /// Fix round 4, item 1 (CRITICAL): the reviewer's exact exploit. A valueless <c>extensions.worktreeConfig</c>,
    /// which git prints under <c>--list -z</c> as a bare key with no newline, turned on per-worktree config; the
    /// worktree's own <c>config.worktree</c>, which <c>--local</c> never reads, then defined a clean filter that
    /// <c>info/attributes</c> applied to every <c>.cs</c> file. Round 3's hand parser skipped the valueless key and
    /// never saw the worktree file, so <see cref="GitCliRunWorkspaceGit.CommitAsync"/> ran the filter.
    /// </summary>
    [Fact]
    public async Task The_valueless_worktreeConfig_exploit_refuses_the_commit_and_the_filter_never_runs()
    {
        var ws = await WorktreeAsync();
        var marker = PlantWorktreeConfigExploit(ws.Root, MirrorOf(), _temp);

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("a valueless extensions.worktreeConfig and every key in worktree scope are outside the allowed surface");
        File.Exists(marker).Should().BeFalse("the filter defined in config.worktree must never execute");
    }

    /// <summary>Fix round 4, item 1: the same exploit, planted after a commit, refuses the push, and the run's branch never reaches the remote.</summary>
    [Fact]
    public async Task The_valueless_worktreeConfig_exploit_refuses_the_push()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        (await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None)).IsSuccess.Should().BeTrue();
        PlantWorktreeConfigExploit(ws.Root, MirrorOf(), _temp);

        var pushed = await _git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue("a valueless extensions.worktreeConfig and every key in worktree scope are outside the allowed surface");
        _remote!.TryHeadOf(ws.Branch).Should().BeNull("a refused push must never reach the remote");
    }

    /// <summary>
    /// Fix round 4, item 2: a valueless <c>[core] symlinks</c> appended after the provider's <c>false</c> is read by
    /// git as <c>true</c>, the last value winning. The check reads the value with git's own <c>--type=bool</c>.
    /// </summary>
    [Fact]
    public async Task A_valueless_core_symlinks_is_refused()
    {
        var ws = await WorktreeAsync();
        File.AppendAllText(Path.Combine(MirrorOf(), "config"), "[core]\n\tsymlinks\n");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("a valueless core.symlinks is true to git, and the provider wrote false");
    }

    /// <summary>
    /// Fix round 5: an unknown <c>extensions.*</c> key on repository format version 1 makes git ignore the mirror, so
    /// the listing holds only command scope and exits 0. The <c>core.bare</c> value read is what refuses it. Without
    /// the gate git still fails later, on staging, so the assertion is on the gate's own refusal, not on failure alone.
    /// </summary>
    [Fact]
    public async Task An_unknown_extension_on_format_version_1_is_refused_by_the_core_bare_read()
    {
        var ws = await WorktreeAsync();
        File.AppendAllText(Path.Combine(MirrorOf(), "config"), "[core]\n\trepositoryformatversion = 1\n[extensions]\n\tthalosunknown = true\n");

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue();
        committed.Error.Code.Should().Be(AgentErrorCode.Validation);
        committed.Error.Message.Should().Contain("core.bare", "the core.bare value read is the check that refuses a repository git ignores");
    }

    /// <summary>Fix round 4, item 4: <c>remote.origin.fetch</c> must appear exactly once, even when the second value is identical to the first.</summary>
    [Fact]
    public async Task A_second_identical_remote_origin_fetch_is_refused()
    {
        var ws = await WorktreeAsync();
        LocalGitRemote.RunGit(MirrorOf(), "config", "--add", "remote.origin.fetch", MirrorConfigSurface.ExpectedFetchRefspec);

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("remote.origin.fetch must be set exactly once");
    }

    /// <summary>
    /// Fix round 4, item 3: a hook in the mirror's default <c>hooks/</c> directory needs no config key at all, so the
    /// config gate cannot see it; only <see cref="GitCli"/>'s <c>-c core.hooksPath</c> pin stops it. The commit must
    /// succeed, so every git call on the commit path really runs with the hooks in place.
    /// </summary>
    [Fact]
    public async Task A_hook_in_the_mirrors_default_hooks_directory_never_runs_on_a_successful_commit()
    {
        var ws = await WorktreeAsync();
        var defaultHooksDir = Path.Combine(MirrorOf(), "hooks");
        Directory.CreateDirectory(defaultHooksDir);
        var markers = new List<string>();
        foreach (var name in new[] { "pre-commit", "commit-msg", "post-commit" })
        {
            var marker = Path.Combine(_temp, name + ".marker").Replace('\\', '/');
            WriteHookScript(defaultHooksDir, name, marker);
            markers.Add(marker);
        }

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue("a hook file needs no config key, so the config gate has nothing to refuse");
        committed.Value.Created.Should().BeTrue();
        markers.Where(File.Exists).Should().BeEmpty("no hook in the mirror's default hooks directory may run");
    }

    /// <summary>
    /// Fix round 4, item 3: <see cref="GitCli"/>'s <c>-c core.fsmonitor=false</c> pin, tested on its own with no config
    /// gate in the way. A scratch repository, which no provider ever validates, configures an fsmonitor hook, and
    /// <c>git add -A</c> through <see cref="GitCli"/> must never run it.
    /// </summary>
    [Fact]
    public async Task GitCli_never_runs_an_fsmonitor_the_repository_configures()
    {
        var scratch = Path.Combine(_temp, "fsmonitor-scratch");
        LocalGitRemote.RunGit(_temp, "init", "-q", scratch);
        var marker = Path.Combine(_temp, "gitcli-fsmonitor.marker");
        LocalGitRemote.RunGit(scratch, "config", "core.fsmonitor", WriteFsmonitorScript(_temp, marker).Replace('\\', '/'));
        File.WriteAllText(Path.Combine(scratch, "code.cs"), "class C {}");
        var git = new GitCli(_options);

        var added = await git.RunAsync(scratch, ["add", "-A"], null, null, CancellationToken.None);
        var status = await git.RunAsync(scratch, ["status", "--porcelain"], null, null, CancellationToken.None);

        added.Succeeded.Should().BeTrue();
        status.Succeeded.Should().BeTrue();
        File.Exists(marker).Should().BeFalse("GitCli pins core.fsmonitor=false on every call, whatever the repository configures");
    }

    /// <summary>
    /// Fix round 4, item 1: the check ignores only <c>command</c> scope, on the premise that the only keys there are
    /// <see cref="GitCli"/>'s own <c>-c</c> pins. This lists them exactly as the check sees them.
    /// </summary>
    [Fact]
    public async Task Command_scope_holds_only_GitClis_own_isolation_pins()
    {
        var ws = await WorktreeAsync();
        var git = new GitCli(_options);

        var listed = await git.RunAsync(ws.Root, ["config", "--list", "--name-only", "--show-scope", "-z"], null, null, CancellationToken.None);

        listed.Succeeded.Should().BeTrue();
        var tokens = listed.StdOut.TrimEnd('\n').Split('\0');
        var commandKeys = Enumerable.Range(0, tokens.Length / 2)
            .Where(i => string.Equals(tokens[2 * i], "command", StringComparison.Ordinal))
            .Select(i => tokens[(2 * i) + 1])
            .ToList();
        commandKeys.Should().BeEquivalentTo(
        [
            "core.hookspath", "core.fsmonitor", "protocol.allow", "protocol.https.allow", "protocol.file.allow",
            "protocol.http.allow", "protocol.ext.allow", "protocol.git.allow", "protocol.ssh.allow", "http.followredirects",
        ]);
    }

    /// <summary>
    /// Plants the reviewer's exploit: a valueless <c>extensions.worktreeConfig</c> in the mirror's config, a
    /// <c>config.worktree</c> in the worktree's own git directory setting <c>core.bare=false</c> and a clean filter
    /// that touches a marker, and <c>*.cs filter=x</c> in the mirror's <c>info/attributes</c>. Returns the marker.
    /// </summary>
    internal static string PlantWorktreeConfigExploit(string worktreeRoot, string mirror, string markerDir)
    {
        var marker = Path.Combine(markerDir, "worktree-filter-ran.marker").Replace('\\', '/');
        File.AppendAllText(Path.Combine(mirror, "config"), "[extensions]\n\tworktreeConfig\n");
        var gitDir = LocalGitRemote.RunGit(worktreeRoot, "rev-parse", "--absolute-git-dir");
        File.WriteAllText(
            Path.Combine(gitDir, "config.worktree"),
            $"[core]\n\tbare = false\n[filter \"x\"]\n\tclean = \"sh -c 'touch {marker}; cat'\"\n");
        Directory.CreateDirectory(Path.Combine(mirror, "info"));
        File.AppendAllText(Path.Combine(mirror, "info", "attributes"), "*.cs filter=x\n");
        return marker;
    }

    /// <summary>Creates a fresh remote and a worktree for a new run id, over this test's own <see cref="_options"/>.</summary>
    private async Task<RunWorkspace> WorktreeAsync(params (string Name, string Content)[] seed)
    {
        _remote = LocalGitRemote.Create(seed);
        var provider = new GitWorktreeWorkspaceProvider(
            _options,
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);
        var runId = Guid.NewGuid();
        var created = await provider.CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", _remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);
        return created.Value;
    }

    /// <summary>
    /// Builds a patch the way a sandbox does: a scratch clone of <see cref="_remote"/> at the workspace's base commit,
    /// <paramref name="stage"/>, which edits and stages, then <c>git diff --cached --binary --full-index &lt;base&gt;</c>.
    /// </summary>
    private string BuildPatch(RunWorkspace ws, Action<string> stage)
    {
        var scratch = Path.Combine(_temp, "scratch-" + Guid.NewGuid().ToString("N"));
        LocalGitRemote.RunGit(_temp, "-c", "core.autocrlf=false", "clone", "-q", _remote!.Url, scratch);
        LocalGitRemote.RunGit(scratch, "config", "core.autocrlf", "false");
        LocalGitRemote.RunGit(scratch, "checkout", "-q", "--detach", ws.BaseCommit!);
        stage(scratch);
        var patch = Path.Combine(_temp, "patch-" + Guid.NewGuid().ToString("N") + ".patch");
        LocalGitRemote.RunGit(scratch, "diff", "--cached", "--binary", "--full-index", "--output=" + patch, ws.BaseCommit!);
        return patch;
    }

    private string MirrorOf() => Path.Combine(_options.DataRoot, "mirrors", "sandbox");

    private static string[] FilesIn(RunWorkspace ws, string sha) =>
        Git(ws.Root, $"show --name-only --format= {sha}").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Runs git and returns trimmed stdout, without throwing on a nonzero exit — e.g. a "key not found" answer from <c>config --get-all</c>.</summary>
    private static string Git(string workingDirectory, string args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdOut = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdOut.Trim();
    }

    /// <summary>Writes a cross-platform hook named <paramref name="hookName"/> under <paramref name="hooksDir"/> that touches <paramref name="markerPath"/> if git ever runs it.</summary>
    private static void WriteHookScript(string hooksDir, string hookName, string markerPath)
    {
        var hookPath = Path.Combine(hooksDir, hookName);
        File.WriteAllText(hookPath, $"#!/bin/sh\ntouch \"{markerPath}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Writes a "spy" git executable that appends its full argv to <paramref name="captureFile"/> and then runs the real git with the same arguments.</summary>
    private static string WriteSpyGit(string dir, string captureFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "spy-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                $"echo %* >> \"{captureFile}\"\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "spy-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            $"echo \"$@\" >> \"{captureFile}\"\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>
    /// Writes a git wrapper that, only for a <c>push</c> invocation, echoes <c>$GIT_CONFIG_VALUE_0</c> (the secret
    /// <see cref="GitCli"/>'s <c>secretConfig</c> mechanism carries) to stderr and exits 1 — a git that actually
    /// leaks the header into its own error output, rather than the real, local file remote every other push test
    /// uses, which never mentions it at all. Every other invocation passes straight through to the real git.
    /// </summary>
    private static string WriteEchoingGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "echo-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "setlocal\r\n" +
                "set ARGS=%*\r\n" +
                "echo %ARGS% | findstr /C:\" push \" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo %GIT_CONFIG_VALUE_0% 1>&2\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "echo-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \" $* \" in\n" +
            "  *\" push \"*)\n" +
            "    echo \"$GIT_CONFIG_VALUE_0\" 1>&2\n" +
            "    exit 1\n" +
            "    ;;\n" +
            "esac\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>Records every URL it is asked for credentials about, for <see cref="Push_asks_the_credential_source_for_the_runs_remote_and_never_stores_the_header"/>.</summary>
    private sealed class RecordingCredentialSource(GitCredentials credentials) : IGitCredentialSource
    {
        public List<string> RequestedUrls { get; } = [];

        public GitCredentials? GetCredentials(string remoteUrl)
        {
            RequestedUrls.Add(remoteUrl);
            return credentials;
        }
    }

    public void Dispose()
    {
        _remote?.Dispose();
        if (!Directory.Exists(_temp))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle on a temp directory is not worth failing the test run over.
        }
    }
}
