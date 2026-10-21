using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Git;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Tests.Git;

/// <summary>
/// <see cref="GitActionTools"/> as the reflective <see cref="LocalToolSource"/> offers them, which is how Daedalus
/// registers the class today, captured before a typed registration replaces it: the <see cref="ToolOracle"/> dump, and
/// the result text of a call to every tool, over service answers holding text the encoder escapes. Characterization
/// tests: they pass on today's code.
/// </summary>
public sealed class GitActionToolGoldenTests
{
    private const string RepoPath = "/repo";

    // Red: in LocalToolSourceBinding.Options, set Description = name; every description in the dump changes.
    [Fact]
    public async Task The_reflective_git_tools_dump_as_before()
    {
        await using var services = Services();
        var dump = ToolOracle.Dump(await ToolsAsync(services));

        dump.Should().Be("""
            create_branch | Create a new local branch and check it out. This is normally the first of four ordered steps for proposing a change: create_branch, then commit, then push, then open_pull_request. Fails if the branch already exists; it never switches to the existing branch instead. | {"type":"object","properties":{"repositoryPath":{"description":"Path to the working tree root (the directory containing .git).","type":"string"},"branchName":{"description":"The branch to create. Must not already exist locally.","type":"string"},"sourceRef":{"description":"Branch, tag, or commit sha to branch from. Omit to branch from the current HEAD.","type":["string","null"],"default":null}},"required":["repositoryPath","branchName"]} | {"type":"string"}
            commit | Stage every pending change (new, modified and deleted files) and commit it on the current branch of the working tree. A commit is local only: nothing anyone else can see changes until you also call git__push on the same working tree. Succeeds without creating a commit when there is nothing to commit. | {"type":"object","properties":{"repositoryPath":{"description":"Path to the working tree root (the directory containing .git).","type":"string"},"message":{"description":"The commit message.","type":"string"},"authorName":{"description":"Author display name. Supply together with authorEmail, or omit both to use the repository\u0027s configured git identity.","type":["string","null"],"default":null},"authorEmail":{"description":"Author email address. Supply together with authorName, or omit both to use the repository\u0027s configured git identity.","type":["string","null"],"default":null}},"required":["repositoryPath","message"]} | {"type":"string"}
            push | Push the current branch to a remote so its commits become visible to anyone with access to the repository. Call this after git__commit — a commit that is never pushed stays invisible to everyone else, and git__open_pull_request needs the branch to already exist on the remote. Creates the upstream tracking branch automatically the first time a new branch is pushed. | {"type":"object","properties":{"repositoryPath":{"description":"Path to the working tree root (the directory containing .git).","type":"string"},"remoteName":{"description":"The remote to push to when the branch has no upstream yet. Omit for \u0027origin\u0027. Ignored once an upstream is already configured.","type":["string","null"],"default":null},"username":{"description":"Username for an HTTP(S) remote (e.g. a fixed placeholder such as x-access-token). Omit for SSH or local remotes.","type":["string","null"],"default":null},"password":{"description":"Password or personal access token for an HTTP(S) remote. Omit for SSH or local remotes.","type":["string","null"],"default":null}},"required":["repositoryPath"]} | {"type":"string"}
            open_pull_request | Open a pull request proposing to merge one branch into another. The source branch must already have been pushed with git__push — a branch that only exists locally cannot be proposed for merge on a hosting platform that has never seen it. This is normally the last of four ordered steps: create_branch, commit, push, then open_pull_request. | {"type":"object","properties":{"repositoryPath":{"description":"Path to the working tree root (the directory containing .git); used to resolve which hosted repository to open the pull request against.","type":"string"},"sourceBranch":{"description":"The branch containing the changes, already pushed to the remote.","type":"string"},"targetBranch":{"description":"The branch the changes should be merged into.","type":"string"},"title":{"description":"The pull request title.","type":"string"},"body":{"description":"The pull request description.","type":"string"}},"required":["repositoryPath","sourceBranch","targetBranch","title","body"]} | {"type":"string"}
            """);
    }

    // Red: in LocalToolSourceBinding.Options, default SerializerOptions to a copy of AIJsonUtilities.DefaultOptions
    // with another Encoder; the escaping of the service text in the results changes.
    [Fact]
    public async Task Every_git_tool_answers_as_before()
    {
        await using var services = Services();
        var tools = (await ToolsAsync(services)).Cast<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal);

        string[] results =
        [
            "create_branch: " + ToolOracle.Result(await tools["create_branch"].InvokeAsync(Args(("repositoryPath", RepoPath), ("branchName", "feature/é")))),
            "commit: " + ToolOracle.Result(await tools["commit"].InvokeAsync(Args(("repositoryPath", RepoPath), ("message", "m"), ("authorName", "A")))),
            "commit created: " + ToolOracle.Result(await tools["commit"].InvokeAsync(Args(("repositoryPath", RepoPath), ("message", "<a & 'é' 🚀>")))),
            "push: " + ToolOracle.Result(await tools["push"].InvokeAsync(Args(("repositoryPath", RepoPath)))),
            "open_pull_request: " + ToolOracle.Result(await tools["open_pull_request"].InvokeAsync(Args(("repositoryPath", RepoPath), ("sourceBranch", "feature/é"), ("targetBranch", "main"), ("title", "t"), ("body", "b")))),
        ];
        var text = string.Join('\n', results);

        text.Should().Be("""
            create_branch: "Created and checked out branch 'feature/é' at abc123."
            commit: "Could not commit: authorName and authorEmail must be supplied together, or both omitted."
            commit created: "Committed def456. Nothing anyone else can see until you call git__push."
            push: "Could not push: Validation: remote said <no> & 'é' \uD83D\uDE80"
            open_pull_request: "Opened pull request: https://example.invalid/pr/1?a=1&b='2'"
            """);
    }

    private static ServiceProvider Services()
    {
        var writer = Substitute.For<IGitWriteService>();
        writer.CreateBranchAsync(RepoPath, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GitBranchResult, AgentError>.Success(new GitBranchResult("feature/é", "abc123")));
        writer.CommitAsync(RepoPath, Arg.Any<string>(), Arg.Any<GitAuthor?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GitCommitResult, AgentError>.Success(new GitCommitResult("def456", Created: true)));
        writer.PushAsync(RepoPath, Arg.Any<string?>(), Arg.Any<GitCredentials?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GitPushResult, AgentError>.Failure(AgentError.Validation("remote said <no> & 'é' 🚀")));
        var publisher = Substitute.For<IPullRequestPublisher>();
        publisher.OpenPullRequestAsync(RepoPath, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<PullRequestResult, AgentError>.Success(new PullRequestResult("https://example.invalid/pr/1?a=1&b='2'", "1")));
        return new ServiceCollection().AddSingleton(writer).AddSingleton(publisher).BuildServiceProvider();
    }

    private static async Task<IReadOnlyList<AITool>> ToolsAsync(IServiceProvider services) =>
        (await new LocalToolSource("git", services, [typeof(GitActionTools)]).GetToolsAsync(CancellationToken.None)).Value;

    private static AIFunctionArguments Args(params (string Name, object? Value)[] arguments)
    {
        var result = new AIFunctionArguments(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            result[name] = value;
        }

        return result;
    }
}
