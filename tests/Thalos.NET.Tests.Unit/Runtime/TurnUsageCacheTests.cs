using Microsoft.Extensions.AI;

namespace Thalos.Tests.Unit.Runtime;

public sealed class TurnUsageCacheTests
{
    [Fact]
    public async Task Turn_usage_sums_cache_read_and_write_tokens_across_round_trips()
    {
        var f = new RuntimeFixture().WithTool(AIFunctionFactory.Create((string text) => "echo:" + text, "echo")).Build();
        f.Client.ThenToolCall("t__echo", new { text = "x" }, cacheRead: 40, cacheWrite: 10).ThenText("done", cacheRead: 60, cacheWrite: 0);
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()), default).ToListAsync();

        var usage = events.OfType<TurnCompletedEvent>().Single().Result.Usage;
        usage.CacheReadTokens.Should().Be(100);
        usage.CacheWriteTokens.Should().Be(10);
        usage.InputTokens.Should().Be(2);
    }

    [Fact]
    public void Operator_plus_sums_cache_read_and_write_tokens()
    {
        var a = new TurnUsage(10, 5, "m") { CacheReadTokens = 40, CacheWriteTokens = 10 };
        var b = new TurnUsage(1, 2, "m") { CacheReadTokens = 60, CacheWriteTokens = 0 };

        var sum = a + b;

        sum.CacheReadTokens.Should().Be(100);
        sum.CacheWriteTokens.Should().Be(10);
    }
}
