using Thalos.Workflow;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>Shared <see cref="RunPrincipal"/> fixtures for tests that need a starter but are not testing who it is.</summary>
internal static class TestPrincipals
{
    public static readonly RunPrincipal Starter = new("test-starter", ["admin"]);

    /// <summary>A resumer or retrier for tests that need one but are not testing who it is.</summary>
    public static readonly RunPrincipal Approver = new("test-approver", ["admin"]);
}
