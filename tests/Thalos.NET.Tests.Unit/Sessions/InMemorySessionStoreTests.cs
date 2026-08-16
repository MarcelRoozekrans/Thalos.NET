using Thalos.Sessions;
using Thalos.Testing;

namespace Thalos.Tests.Unit.Sessions;

public sealed class InMemorySessionStoreTests : SessionStoreContractTests
{
    protected override ValueTask<IAgentSessionStore> CreateStoreAsync(TimeProvider clock) =>
        new(new InMemorySessionStore(clock));
}
