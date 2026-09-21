using NSubstitute;
using Thalos.Git;
using ZeroAlloc.Results;

namespace Thalos.Tests.Git;

/// <summary>Tests for <see cref="GitActionTools"/> over fake <see cref="IGitWriteService"/>/<see cref="IPullRequestPublisher"/> dependencies.</summary>
public sealed class GitActionToolsTests
{
    private const string RepoPath = "C:/repo";

    private static (GitActionTools Tools, IGitWriteService Writer, IPullRequestPublisher Publisher) Build()
    {
        var writer = Substitute.For<IGitWriteService>();
        var publisher = Substitute.For<IPullRequestPublisher>();
        return (new GitActionTools(writer, publisher), writer, publisher);
    }

    // Breaks if the success message stops reporting the branch name and sha the service actually returned.
    [Fact]
    public async Task CreateBranch_reports_the_branch_and_sha_on_success()
    {
        var (tools, writer, _) = Build();
        writer.CreateBranchAsync(RepoPath, "feature/x", null, Arg.Any<CancellationToken>())
            .Returns(Result<GitBranchResult, AgentError>.Success(new GitBranchResult("feature/x", "abc123")));

        var text = await tools.CreateBranch(RepoPath, "feature/x");

        text.Should().Contain("feature/x").And.Contain("abc123");
    }

    // Breaks if a failed CreateBranchAsync is swallowed or thrown instead of surfaced to the caller as text.
    [Fact]
    public async Task CreateBranch_reports_the_service_error_on_failure()
    {
        var (tools, writer, _) = Build();
        writer.CreateBranchAsync(RepoPath, "feature/dup", null, Arg.Any<CancellationToken>())
            .Returns(Result<GitBranchResult, AgentError>.Failure(AgentError.GitBranchAlreadyExists("feature/dup")));

        var text = await tools.CreateBranch(RepoPath, "feature/dup");

        text.Should().Contain("Could not create the branch").And.Contain("feature/dup");
    }

    // Breaks if the default sourceRef stops being passed through as null (e.g. defaulted to empty string instead).
    [Fact]
    public async Task CreateBranch_passes_a_null_sourceRef_when_none_is_given()
    {
        var (tools, writer, _) = Build();
        writer.CreateBranchAsync(RepoPath, "feature/x", null, Arg.Any<CancellationToken>())
            .Returns(Result<GitBranchResult, AgentError>.Success(new GitBranchResult("feature/x", "abc123")));

        await tools.CreateBranch(RepoPath, "feature/x");

        await writer.Received(1).CreateBranchAsync(RepoPath, "feature/x", null, Arg.Any<CancellationToken>());
    }

    // Breaks if the partial-author guard is removed: a half-supplied identity would otherwise reach the service,
    // which cannot distinguish "caller meant to omit it" from "caller made a typo in one field".
    [Fact]
    public async Task Commit_rejects_a_partial_author_without_calling_the_service()
    {
        var (tools, writer, _) = Build();

        var text = await tools.Commit(RepoPath, "msg", authorName: "Ada");

        text.Should().Contain("authorName and authorEmail must be supplied together");
        await writer.DidNotReceive().CommitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<GitAuthor?>(), Arg.Any<CancellationToken>());
    }

    // Breaks if omitting both author fields stops mapping to a null GitAuthor (the service's documented signal to fall back to git config).
    [Fact]
    public async Task Commit_passes_a_null_author_when_both_fields_are_omitted()
    {
        var (tools, writer, _) = Build();
        writer.CommitAsync(RepoPath, "msg", null, Arg.Any<CancellationToken>())
            .Returns(Result<GitCommitResult, AgentError>.Success(new GitCommitResult("sha1", true)));

        await tools.Commit(RepoPath, "msg");

        await writer.Received(1).CommitAsync(RepoPath, "msg", null, Arg.Any<CancellationToken>());
    }

    // Breaks if a fully supplied author is dropped instead of forwarded as a GitAuthor.
    [Fact]
    public async Task Commit_forwards_the_author_when_both_fields_are_supplied()
    {
        var (tools, writer, _) = Build();
        writer.CommitAsync(RepoPath, "msg", Arg.Any<GitAuthor?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GitCommitResult, AgentError>.Success(new GitCommitResult("sha1", true)));

        await tools.Commit(RepoPath, "msg", "Ada Lovelace", "ada@example.com");

        await writer.Received(1).CommitAsync(RepoPath, "msg", new GitAuthor("Ada Lovelace", "ada@example.com"), Arg.Any<CancellationToken>());
    }

    // Breaks if "nothing to commit" (Created: false) is reported as though a new commit was made.
    [Fact]
    public async Task Commit_reports_nothing_to_commit_when_Created_is_false()
    {
        var (tools, writer, _) = Build();
        writer.CommitAsync(RepoPath, "msg", null, Arg.Any<CancellationToken>())
            .Returns(Result<GitCommitResult, AgentError>.Success(new GitCommitResult("sha0", false)));

        var text = await tools.Commit(RepoPath, "msg");

        text.Should().Contain("Nothing to commit");
    }

    // Breaks if a real commit's sha stops appearing in the confirmation, or if the "not yet pushed" reminder is dropped.
    [Fact]
    public async Task Commit_reports_the_new_sha_and_that_it_is_not_yet_visible_when_Created_is_true()
    {
        var (tools, writer, _) = Build();
        writer.CommitAsync(RepoPath, "msg", null, Arg.Any<CancellationToken>())
            .Returns(Result<GitCommitResult, AgentError>.Success(new GitCommitResult("sha9", true)));

        var text = await tools.Commit(RepoPath, "msg");

        text.Should().Contain("sha9").And.Contain("git__push");
    }

    // Breaks if the partial-credentials guard is removed, letting a half-supplied credential reach the service.
    [Fact]
    public async Task Push_rejects_partial_credentials_without_calling_the_service()
    {
        var (tools, writer, _) = Build();

        var text = await tools.Push(RepoPath, username: "x-access-token");

        text.Should().Contain("username and password must be supplied together");
        await writer.DidNotReceive().PushAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<GitCredentials?>(), Arg.Any<CancellationToken>());
    }

    // Breaks if UpstreamCreated is dropped from the confirmation, hiding whether tracking was just established.
    [Fact]
    public async Task Push_reports_that_the_upstream_was_created_when_UpstreamCreated_is_true()
    {
        var (tools, writer, _) = Build();
        writer.PushAsync(RepoPath, null, null, Arg.Any<CancellationToken>())
            .Returns(Result<GitPushResult, AgentError>.Success(new GitPushResult("origin", "feature/x", true)));

        var text = await tools.Push(RepoPath);

        text.Should().Contain("created the upstream").And.Contain("origin/feature/x");
    }

    // Breaks if a push to an already-tracked branch is misreported as creating a new upstream.
    [Fact]
    public async Task Push_reports_a_plain_push_when_UpstreamCreated_is_false()
    {
        var (tools, writer, _) = Build();
        writer.PushAsync(RepoPath, null, null, Arg.Any<CancellationToken>())
            .Returns(Result<GitPushResult, AgentError>.Success(new GitPushResult("origin", "feature/x", false)));

        var text = await tools.Push(RepoPath);

        text.Should().Contain("Pushed to 'origin/feature/x'").And.NotContain("created the upstream");
    }

    // Breaks if a failed PushAsync (e.g. missing remote) is swallowed or thrown instead of surfaced as text.
    [Fact]
    public async Task Push_reports_the_service_error_on_failure()
    {
        var (tools, writer, _) = Build();
        writer.PushAsync(RepoPath, null, null, Arg.Any<CancellationToken>())
            .Returns(Result<GitPushResult, AgentError>.Failure(AgentError.GitOperationFailed("no remote configured")));

        var text = await tools.Push(RepoPath);

        text.Should().Contain("Could not push").And.Contain("no remote configured");
    }

    // Breaks if the pull request's URL stops appearing in the confirmation.
    [Fact]
    public async Task OpenPullRequest_reports_the_url_on_success()
    {
        var (tools, _, publisher) = Build();
        publisher.OpenPullRequestAsync(RepoPath, "feature/x", "main", "Title", "Body", Arg.Any<CancellationToken>())
            .Returns(Result<PullRequestResult, AgentError>.Success(new PullRequestResult("https://example.invalid/pr/1", "1")));

        var text = await tools.OpenPullRequest(RepoPath, "feature/x", "main", "Title", "Body");

        text.Should().Contain("https://example.invalid/pr/1");
    }

    // Breaks if a failed OpenPullRequestAsync is swallowed or thrown instead of surfaced to the caller as text.
    [Fact]
    public async Task OpenPullRequest_reports_the_publisher_error_on_failure()
    {
        var (tools, _, publisher) = Build();
        publisher.OpenPullRequestAsync(RepoPath, "feature/x", "main", "Title", "Body", Arg.Any<CancellationToken>())
            .Returns(Result<PullRequestResult, AgentError>.Failure(AgentError.GitOperationFailed("host unreachable")));

        var text = await tools.OpenPullRequest(RepoPath, "feature/x", "main", "Title", "Body");

        text.Should().Contain("Could not open the pull request").And.Contain("host unreachable");
    }
}
