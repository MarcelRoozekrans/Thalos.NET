using System.Security.AccessControl;
using System.Security.Principal;

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

    /// <summary>
    /// Makes one sub-folder unlistable for the length of the returned scope — the ACL change, unmounted share or
    /// antivirus lock the loader has to survive. Windows uses a deny ACE for the current user (no elevation needed),
    /// Unix a mode of 000; either way the denial is verified before the test runs, so it can never pass vacuously.
    /// </summary>
    public IDisposable DenyAccess(string relativeFolder)
    {
        var path = Path.Combine(Root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        var scope = OperatingSystem.IsWindows() ? DenyOnWindows(path) : DenyOnUnix(path);
        try
        {
            _ = Directory.EnumerateFiles(path).GetEnumerator().MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return scope;
        }

        scope.Dispose();
        throw new InvalidOperationException($"'{path}' is still readable after access was denied, so the test would prove nothing. Run it as an ordinary user.");
    }

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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Restore DenyOnWindows(string path)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Restore(() =>
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        });
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Restore DenyOnUnix(string path)
    {
        var previous = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new Restore(() => File.SetUnixFileMode(path, previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static string Content(string name, string description, string body, string? tags) =>
        "---\nname: " + name + "\ndescription: " + description + (tags is null ? "" : "\ntags: " + tags) + "\n---\n\n" + body + "\n";
}
