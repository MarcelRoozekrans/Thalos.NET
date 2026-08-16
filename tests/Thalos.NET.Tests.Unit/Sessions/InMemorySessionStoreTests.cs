using Thalos.Sessions;
using Thalos.Testing;

namespace Thalos.Tests.Unit.Sessions;

public sealed class InMemorySessionStoreTests : SessionStoreContractTests
{
    protected override ValueTask<IAgentSessionStore> CreateStoreAsync() =>
        new(new InMemorySessionStore(TimeProvider.System));
}
