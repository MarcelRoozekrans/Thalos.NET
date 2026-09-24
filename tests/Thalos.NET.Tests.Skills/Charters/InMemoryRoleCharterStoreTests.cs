using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills.Charters;

public sealed class InMemoryRoleCharterStoreTests : Thalos.Testing.RoleCharterStoreContractTests
{
    protected override ValueTask<IRoleCharterStore> CreateStoreAsync(TimeProvider clock) => new(new InMemoryRoleCharterStore(clock));
}
