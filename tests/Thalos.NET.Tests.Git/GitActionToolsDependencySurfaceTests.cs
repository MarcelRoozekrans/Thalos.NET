using System.Reflection;
using Thalos.Git;

namespace Thalos.Tests.Git;

/// <summary>
/// Guards the boundary <see cref="GitActionTools"/>'s own doc comment states: it may depend only on the two
/// write-only surfaces it needs, so it can never be handed — accidentally or by a future change — a way to read a
/// repository, a diff, commit history, or a pull request's contents. Each rule below was proved able to fail by
/// temporarily adding a throwaway read-shaped dependency to <see cref="GitActionTools"/>, observing both tests go
/// red, then reverting; see the task report for the exact failure text.
/// </summary>
public sealed class GitActionToolsDependencySurfaceTests
{
    private static readonly Type[] AllowedDependencies = [typeof(IGitWriteService), typeof(IPullRequestPublisher)];

    // Naming patterns for anything that hands back information rather than performing an action. Deliberately a
    // denylist over method-name verbs rather than an allowlist over method names, so a newly added method on either
    // allowed interface is caught by the same rule without this test needing to change.
    private static readonly string[] ReadVerbs = ["Get", "List", "Read", "Find", "Query", "Fetch", "Retrieve", "Search"];

    // Breaks if GitActionTools grows a third constructor dependency of any kind (read or write) — an addition here
    // silently widens what the class can reach, which is exactly what the doc comment says must not happen.
    [Fact]
    public void Constructor_depends_only_on_the_two_declared_write_only_surfaces()
    {
        var parameterTypes = SoleConstructorParameterTypes();

        parameterTypes.Should().BeEquivalentTo(AllowedDependencies,
            "GitActionTools must depend on exactly IGitWriteService and IPullRequestPublisher and nothing else");
    }

    // Breaks if either allowed interface (or a replacement for one of them) grows a method shaped like a read —
    // the class would then be able to reach that read surface through a dependency it already legitimately holds.
    [Fact]
    public void None_of_the_constructor_dependencies_expose_a_read_shaped_method()
    {
        var offenders = new List<string>();
        foreach (var parameterType in SoleConstructorParameterTypes())
        {
            foreach (var method in parameterType.GetMethods())
            {
                if (Array.Exists(ReadVerbs, verb => method.Name.StartsWith(verb, StringComparison.Ordinal)))
                {
                    offenders.Add($"{parameterType.Name}.{method.Name}");
                }
            }
        }

        offenders.Should().BeEmpty("GitActionTools's dependencies must stay write-only; a read-shaped method on any of them is a path back to a read surface");
    }

    private static Type[] SoleConstructorParameterTypes()
    {
        var constructors = typeof(GitActionTools).GetConstructors();
        constructors.Should().HaveCount(1, "GitActionTools should have exactly one public constructor");
        return Array.ConvertAll(constructors[0].GetParameters(), p => p.ParameterType);
    }
}
