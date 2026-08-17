using Thalos.Memory;
using Thalos.Testing;

namespace Thalos.Tests.Memory;

public sealed class InMemoryMemoryStoreTests : MemoryStoreContractTests
{
    protected override ValueTask<IMemoryStore> CreateStoreAsync(TimeProvider clock) => new(new InMemoryMemoryStore(clock));
}
