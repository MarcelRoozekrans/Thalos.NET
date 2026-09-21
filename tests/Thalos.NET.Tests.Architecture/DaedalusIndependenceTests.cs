using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Thalos.Tests.Architecture;

/// <summary>
/// Proves the premise <c>Thalos.NET.Git</c>'s own split rests on: that a consumer who wants git write tools without
/// the native libgit2 dependency — or any Thalos capability at all — is not secretly also depending on the
/// Daedalus host this repository happens to be developed alongside. No project or build file anywhere in this
/// repository may carry a package or project reference to Daedalus, and no C# source file anywhere in it may
/// declare or use a Daedalus type. This failure mode is silent: nothing else breaks, no build fails, and no other
/// test goes red right up until someone actually tries to consume Thalos.NET standalone, which is the whole
/// premise a package split like this one exists to serve.
/// </summary>
public sealed class DaedalusIndependenceTests
{
    private static readonly string[] ProjectFilePatterns = ["*.csproj", "*.props", "*.targets"];

    [Fact]
    public void No_project_or_build_file_in_the_repository_references_a_Daedalus_package_or_project()
    {
        var projectFiles = ProjectFilePatterns.SelectMany(p => FindFiles(RepoRoot(), p)).ToArray();

        // A scan that silently enumerated zero files would pass having checked nothing — the exact vacuous-pass
        // failure mode this guard exists to avoid.
        projectFiles.Should().NotBeEmpty("expected to find at least one .csproj/.props/.targets file under the repository root");

        var offendingReferences = new List<string>();
        foreach (var path in projectFiles)
        {
            var doc = XDocument.Load(path);
            foreach (var element in doc.Descendants())
            {
                if (element.Name.LocalName is not ("ProjectReference" or "PackageReference" or "GlobalPackageReference" or "Reference"))
                {
                    continue;
                }

                var include = element.Attribute("Include")?.Value;
                if (include is not null && include.Contains("Daedalus", StringComparison.OrdinalIgnoreCase))
                {
                    offendingReferences.Add($"{Path.GetRelativePath(RepoRoot(), path)}: {element.Name.LocalName}={include}");
                }
            }
        }

        offendingReferences.Should().BeEmpty(
            "no project or build file in this repository may reference Daedalus by package or project — Thalos.NET must be consumable standalone");
    }

    [Fact]
    public void No_Csharp_source_in_the_repository_declares_or_uses_a_Daedalus_type()
    {
        var sourceFiles = FindFiles(RepoRoot(), "*.cs");
        sourceFiles.Should().NotBeEmpty("expected to find at least one .cs file under the repository root");

        var totalNameReferences = 0;
        var offendingFiles = new List<string>();

        foreach (var path in sourceFiles)
        {
            var text = File.ReadAllText(path);
            var root = CSharpSyntaxTree.ParseText(text, path: path).GetRoot();

            // The precise line between a "reference" and a "mention": a SimpleNameSyntax (IdentifierNameSyntax or
            // GenericNameSyntax) is how C# spells USING an existing name — a `using Daedalus.Foo;`, a
            // fully-qualified `Daedalus.Foo` type, a member access, an invocation. It is categorically different
            // from the identifier token on a DECLARATION (a class, method, parameter or local variable name), which
            // never becomes a SimpleNameSyntax node — only a plain token on the declaring node. That is what lets
            // this very file declare a class named DaedalusIndependenceTests and methods that talk about "Daedalus"
            // by name, and this repo's XML-doc prose (Thalos.NET.Testing's skill contract tests among others,
            // structured trivia that DescendantNodes() does not walk into by default) mention Daedalus in prose,
            // without either tripping this check — while a real `using Daedalus...;` or `Daedalus.SomeType` would.
            var foundOffendingReference = false;
            foreach (var node in root.DescendantNodes())
            {
                if (node is not SimpleNameSyntax simpleName)
                {
                    continue;
                }

                totalNameReferences++;
                if (simpleName.Identifier.ValueText.Contains("Daedalus", StringComparison.Ordinal))
                {
                    foundOffendingReference = true;
                }
            }

            if (foundOffendingReference)
            {
                offendingFiles.Add(Path.GetRelativePath(RepoRoot(), path));
            }
        }

        // Guards the scan itself, the same way the file-count check above does: if source files had been read back
        // empty (or parsing had silently produced empty name-reference nodes), the assertion below would pass
        // having examined nothing.
        totalNameReferences.Should().BeGreaterThan(1000, "expected a substantial number of name-reference nodes across the repository's C# source");

        offendingFiles.Should().BeEmpty(
            "no C# source in this repository may declare or reference a Daedalus type — Thalos.NET must be consumable standalone");
    }

    private static string[] FindFiles(string root, string searchPattern) =>
        Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
            .Where(p => !IsUnderExcludedDirectory(p, root))
            .ToArray();

    private static bool IsUnderExcludedDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj" or ".git");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Thalos.NET.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("expected to find Thalos.NET.slnx walking up from the test output directory");
        return dir!.FullName;
    }
}
