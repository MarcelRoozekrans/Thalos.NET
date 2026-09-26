using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Testing;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Mcp;

/// <summary>
/// A run-scoped server composed the way a host composes it: <see cref="McpThalosBuilderExtensions.AddMcpServer"/> with
/// the real git workspace provider in one container, and turns run through the real runtime over a scripted model.
/// </summary>
public sealed class RunScopedMcpCompositionTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("thalos-run-compose-").FullName;

    [Fact]
    public async Task A_run_turn_reaches_its_own_server_and_a_chat_turn_the_host_server_through_the_runtime()
    {
        using var remote = LocalGitRemote.Create();
        var client = new ScriptedChatClient();
        var agent = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i", Tools = ["roslyn__*"] };
        var definition = McpServerFixture.Definition("--host");
        definition.RunScoped = new RunScopedMcpDefinition { Args = [McpServerFixture.ServerDll, "--run", "--id", "${run.id}"] };
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t
            .UseChatClientProvider(Provider(client))
            .UseInMemorySessionStore()
            .AddAgent(agent)
            .AddMcpServer("roslyn", definition)
            .UseGitWorktreeWorkspaces(o => o.DataRoot = Path.Combine(_root, "data")));
        await using var sp = services.BuildServiceProvider();

        // The provider observes the registry and the registry finds the provider: resolving either must not wait on the other.
        var resolving = Task.Run(() => sp.GetRequiredService<IRunWorkspaceProvider>());
        (await Task.WhenAny(resolving, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(resolving, "the registry must not resolve the provider while the provider is resolving its observers");
        var provider = await resolving;
        var runId = Guid.NewGuid();
        var created = await provider.CreateAsync(new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null), CancellationToken.None);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : "");
        var ready = await sp.GetRequiredService<IRunToolServerReadiness>().WaitAllReadyAsync(runId, TimeSpan.FromSeconds(30), CancellationToken.None);
        ready.IsSuccess.Should().BeTrue(ready.IsFailure ? ready.Error.Message : "the provider told the same registry the tools route through");

        try
        {
            using var _scope = new AssertionScope();
            var runResult = await ToolResultOfTurnAsync(sp, client, agent, TestCallers.RunCaller(runId));
            runResult.Should().Contain($"--id {runId:D}");
            runResult.Should().NotContain("--host");

            var chatResult = await ToolResultOfTurnAsync(sp, client, agent, new TestCaller("chat-user"));
            chatResult.Should().Contain("--host");
            chatResult.Should().NotContain("--run");
        }
        finally
        {
            (await provider.RemoveAsync(runId, CancellationToken.None)).IsSuccess.Should().BeTrue("the run's server is stopped before its worktree is removed");
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git writes read-only object files
        }

        await TestDirectories.DeleteAsync(_root);
    }

    /// <summary>Runs one turn in which the model calls <c>roslyn__args</c>; returns what the tool put back into the conversation.</summary>
    private static async Task<string> ToolResultOfTurnAsync(IServiceProvider sp, ScriptedChatClient client, AgentDefinition agent, ISecurityContext caller)
    {
        var before = client.Requests.Count;
        client.ThenToolCall("roslyn__args", new { }).ThenText("done");
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var session = await runtime.CreateSessionAsync(agent.Id, caller, CancellationToken.None);
        session.IsSuccess.Should().BeTrue(session.IsFailure ? session.Error.ToString() : "");
        var turn = await runtime.RunTurnAsync(new AgentTurnRequest(session.Value, "go", caller), CancellationToken.None);
        turn.IsSuccess.Should().BeTrue(turn.IsFailure ? turn.Error.ToString() : "");

        return client.Requests[before + 1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
    }

    private static IChatClientProvider Provider(IChatClient client)
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake");
        provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(client);
        return provider;
    }
}
