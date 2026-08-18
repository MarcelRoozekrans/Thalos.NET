using System.Diagnostics;
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
    /// antivirus lock the loader has to survive. Unix uses a mode of 000; Windows uses a deny ACE for the current
    /// user, falling back to a dangling junction when that ACE does not bite, which is the case whenever the tests
    /// run elevated (a CI runner is typically an administrator, and administrators bypass DACLs). A junction needs
    /// no privilege to create and defeats an administrator too, because its target genuinely does not exist rather
    /// than being merely forbidden. Either way the denial is verified below, so this can never pass vacuously.
    /// </summary>
    public IDisposable DenyAccess(string relativeFolder)
    {
        var path = Path.Combine(Root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        var scope = OperatingSystem.IsWindows() ? DenyOnWindows(path) : DenyOnUnix(path);
        if (Unlistable(path))
        {
            return scope;
        }

        scope.Dispose();
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException($"'{path}' is still listable after its mode was set to 000, so the test would prove nothing. Do not run the suite as root.");
        }

        var junction = DanglingJunction(path);
        if (Unlistable(path))
        {
            return junction;
        }

        junction.Dispose();
        throw new InvalidOperationException($"'{path}' is still listable after both a deny ACE and a dangling junction, so the test would prove nothing.");
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

    private static bool Unlistable(string path)
    {
        try
        {
            _ = Directory.EnumerateFiles(path).GetEnumerator().MoveNext();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Replaces <paramref name="path"/> with a junction to a target that does not exist, and restores the folder on dispose.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Restore DanglingJunction(string path)
    {
        // Outside the skills root: a sibling would be scanned as another skill folder.
        var saved = Path.Combine(Path.GetTempPath(), "thalos-skills-saved-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, saved);
        Run("cmd.exe", $"/c mklink /J \"{path}\" \"{path}.no-such-target\"");
        return new Restore(() =>
        {
            Directory.Delete(path);
            Directory.Move(saved, path);
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
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
