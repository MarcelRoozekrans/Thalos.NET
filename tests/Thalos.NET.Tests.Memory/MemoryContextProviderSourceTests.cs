using Microsoft.Extensions.Options;
using Thalos.Memory;

namespace Thalos.Tests.Memory;

public sealed class MemoryContextProviderSourceTests
{
    private static AgentDefinition Def(AgentMemorySettings? memory = null) => new() { Id = AgentId.New(), Name = "a", Instructions = "i", Memory = memory };

    private static MemoryContextProviderSource Source(MemoryOptions options)
    {
        var f = new MemoryServiceFixture();
        return new MemoryContextProviderSource(f.Build(), Options.Create(options), f.Clock, f.Hub);
    }

    [Fact]
    public void Enabled_by_default_and_per_agent_overrides()
    {
        Source(new MemoryOptions()).CreateProvider(Def()).Should().BeOfType<MemoryContextProvider>();
        Source(new MemoryOptions()).CreateProvider(Def(new AgentMemorySettings { Enabled = false })).Should().BeNull();
        Source(new MemoryOptions { Enabled = false }).CreateProvider(Def()).Should().BeNull();
        Source(new MemoryOptions { Enabled = false }).CreateProvider(Def(new AgentMemorySettings { Enabled = true })).Should().BeOfType<MemoryContextProvider>("an agent may opt in");
    }

    [Fact]
    public void Per_agent_TopK_is_applied_on_a_fresh_RecallOptions_and_clamped_to_one()
    {
        var options = new MemoryOptions { Recall = { TopK = 7, MinScore = 0.3, MaxChars = 123 } };
        var bound = options.Recall;

        var overridden = (MemoryContextProvider)Source(options).CreateProvider(Def(new AgentMemorySettings { TopK = 2 }))!;
        var clamped = (MemoryContextProvider)Source(options).CreateProvider(Def(new AgentMemorySettings { TopK = 0 }))!;
        var inherited = (MemoryContextProvider)Source(options).CreateProvider(Def())!;

        overridden.Recall.Should().NotBeSameAs(bound).And.BeEquivalentTo(new RecallOptions { TopK = 2, MinScore = 0.3, MaxChars = 123 });
        clamped.Recall.TopK.Should().Be(1);
        inherited.Recall.Should().NotBeSameAs(bound).And.BeEquivalentTo(new RecallOptions { TopK = 7, MinScore = 0.3, MaxChars = 123 });
        bound.TopK.Should().Be(7, "the bound options instance is never mutated");
    }
}
