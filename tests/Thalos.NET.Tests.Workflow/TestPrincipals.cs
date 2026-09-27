using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>Shared <see cref="RunPrincipal"/> fixtures for tests that need a starter but are not testing who it is.</summary>
internal static class TestPrincipals
{
    public static readonly RunPrincipal Starter = new("test-starter", ["admin"]);
}
