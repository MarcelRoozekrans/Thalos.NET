using Thalos.Skills;

namespace Thalos.Tests.Skills;

public sealed class SkillFileLoaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Enumerate_finds_folder_skills_and_flat_skills_in_a_stable_order()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release");
        folder.WriteFlatSkill("notes");
        folder.WriteRaw("release/notes.txt", "ignored");
        folder.WriteRaw("release/deeper/SKILL.md", "ignored — only one level down is scanned");

        var files = SkillFileLoader.Enumerate(folder.Root);

        files.IsSuccess.Should().BeTrue();
        files.Value.Select(f => SkillFileLoader.RelativePath(folder.Root, f)).Should().Equal(["notes.md", "release/SKILL.md"]);
    }

    [Fact]
    public void Enumerate_orders_by_the_root_relative_path_so_windows_and_linux_agree()
    {
        // '/' (U+002F) sorts before '0' (U+0030) but '\' (U+005C) sorts after it, so ordering full paths
        // ordinally would put these two in a different order on Windows than on Linux.
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("a");
        folder.WriteFlatSkill("a0b");

        var files = SkillFileLoader.Enumerate(folder.Root);

        files.Value.Select(f => SkillFileLoader.RelativePath(folder.Root, f)).Should().Equal(["a/SKILL.md", "a0b.md"]);
    }

    [Fact]
    public void Enumerate_matches_the_file_name_and_extension_case_sensitively_on_every_os()
    {
        using var folder = new SkillFolder();
        folder.WriteRaw("lower/skill.md", "---\nname: lower\ndescription: x\n---\nbody\n");
        folder.WriteRaw("SHOUTED.MD", "---\nname: shouted\ndescription: x\n---\nbody\n");
        folder.WriteFolderSkill("kept");

        var files = SkillFileLoader.Enumerate(folder.Root);

        files.Value.Select(f => SkillFileLoader.RelativePath(folder.Root, f)).Should().Equal(["kept/SKILL.md"]);
    }

    [Fact]
    public void Enumerate_keeps_the_root_when_one_subfolder_cannot_be_listed()
    {
        // An ACL change, an unmounted share or an antivirus lock on one folder must cost that one skill.
        // Failing the whole root would skip the deactivation sweep for every root and, before that guard
        // existed, retire every skill the root contributed.
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("locked");
        folder.WriteFolderSkill("release");
        folder.WriteFlatSkill("notes");

        using var denied = folder.DenyAccess("locked");
        var files = SkillFileLoader.Enumerate(folder.Root);

        files.IsSuccess.Should().BeTrue(files.IsFailure ? files.Error.ToString() : "");
        files.Value.Select(f => SkillFileLoader.RelativePath(folder.Root, f))
            .Should().Equal(["locked/SKILL.md", "notes.md", "release/SKILL.md"], "the unreadable folder is offered as one candidate file, which LoadAsync then reports as skipped");
    }

    [Fact]
    public void Enumerate_reports_a_missing_or_unreadable_root_instead_of_throwing()
    {
        var result = SkillFileLoader.Enumerate(Path.Combine(Path.GetTempPath(), "thalos-skills-does-not-exist-" + Guid.NewGuid().ToString("N")));
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SkillValidationFailed);
        result.Error.Message.Should().Contain("does not exist");
    }

    [Fact]
    public async Task LoadAsync_derives_the_name_from_the_folder_or_the_file_name_case_insensitively()
    {
        using var folder = new SkillFolder();
        var fromFolder = folder.WriteFolderSkill("release");
        var flat = await LoadAsync(folder, folder.WriteFlatSkill("notes"));
        var cased = folder.WriteRaw("Dotnet-Migrations/SKILL.md", "---\nname: dotnet-migrations\ndescription: x\n---\nbody\n");

        (await LoadAsync(folder, fromFolder)).Value.Name.Value.Should().Be("release");
        flat.Value.Name.Value.Should().Be("notes");
        flat.Value.SourcePath.Should().Be("notes.md");
        (await LoadAsync(folder, cased)).IsSuccess.Should().BeTrue("the folder name is lower-cased before it is compared");
    }

    [Fact]
    public async Task A_name_that_disagrees_with_the_path_is_a_load_error()
    {
        using var folder = new SkillFolder();
        var path = folder.WriteFolderSkill("release", frontmatterName: "releases");
        var result = await LoadAsync(folder, path);
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("release/SKILL.md").And.Contain("does not match the file or folder name");
    }

    [Fact]
    public async Task A_SKILL_md_directly_under_the_root_is_a_load_error()
    {
        using var folder = new SkillFolder();
        var path = folder.WriteRaw("SKILL.md", "---\nname: skill\ndescription: x\n---\nbody\n");
        var result = await LoadAsync(folder, path);
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("must live in a folder named after the skill");
    }

    [Fact]
    public async Task A_file_over_the_byte_cap_is_rejected_without_being_read()
    {
        using var folder = new SkillFolder();
        var path = folder.WriteRaw("huge/SKILL.md", "---\nname: huge\ndescription: x\n---\n" + new string('b', SkillFileLoader.MaxFileBytes + 10));
        var result = await LoadAsync(folder, path);
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("huge/SKILL.md").And.Contain("the limit is");
    }

    [Fact]
    public async Task A_file_that_vanished_after_enumeration_is_a_failure_not_an_exception()
    {
        using var folder = new SkillFolder();
        var path = folder.WriteFolderSkill("gone");
        folder.Delete("gone/SKILL.md");

        var result = await LoadAsync(folder, path);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SkillValidationFailed);
        result.Error.Message.Should().Contain("gone/SKILL.md").And.Contain("could not be read");
    }

    [Fact]
    public async Task A_file_whose_path_names_no_skill_is_a_load_error_not_an_argument_exception()
    {
        using var folder = new SkillFolder();
        var path = folder.WriteRaw(".md", "---\nname: x\ndescription: x\n---\nbody\n");

        var result = await LoadAsync(folder, path);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("names no skill");
    }

    private static ValueTask<ZeroAlloc.Results.Result<SkillDocument, AgentError>> LoadAsync(SkillFolder folder, string path) =>
        SkillFileLoader.LoadAsync(folder.Root, path, Now, CancellationToken.None);
}
