namespace Thalos.Tests.Skills;

/// <summary>A throw-away skills root under the system temp folder; <c>Dispose</c> deletes it.</summary>
internal sealed class SkillFolder : IDisposable
{
    public SkillFolder(string? label = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "thalos-skills-" + (label ?? "t") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Writes <c>&lt;Root&gt;/&lt;name&gt;/SKILL.md</c> and returns its full path.</summary>
    public string WriteFolderSkill(string name, string description = "A procedure.", string body = "# Do it\n1. Step.", string? tags = null, string? frontmatterName = null)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(path, Content(frontmatterName ?? name, description, body, tags));
        return path;
    }

    /// <summary>Writes <c>&lt;Root&gt;/&lt;name&gt;.md</c> and returns its full path.</summary>
    public string WriteFlatSkill(string name, string description = "A procedure.", string body = "# Do it\n1. Step.", string? tags = null, string? frontmatterName = null)
    {
        var path = Path.Combine(Root, name + ".md");
        File.WriteAllText(path, Content(frontmatterName ?? name, description, body, tags));
        return path;
    }

    /// <summary>Writes an arbitrary file under the root (used for malformed input).</summary>
    public string WriteRaw(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Delete(string relativePath) => File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder that will not delete is not a test failure
        }
    }

    private static string Content(string name, string description, string body, string? tags) =>
        "---\nname: " + name + "\ndescription: " + description + (tags is null ? "" : "\ntags: " + tags) + "\n---\n\n" + body + "\n";
}
