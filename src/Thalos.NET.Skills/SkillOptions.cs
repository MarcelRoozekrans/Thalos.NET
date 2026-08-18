namespace Thalos.Skills;

/// <summary>Host-wide skills configuration (section <c>Thalos:Skills</c>).</summary>
public sealed class SkillOptions
{
    /// <summary>Configuration section bound by <c>UseSkills(IConfiguration)</c>.</summary>
    public const string SectionName = "Thalos:Skills";

    /// <summary>Master switch for the catalogue, the tools and the start-up sync.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Folders scanned for <c>&lt;name&gt;/SKILL.md</c> and <c>&lt;name&gt;.md</c>, in precedence order: on a duplicate name the first root wins.</summary>
    public IList<string> Roots { get; set; } = [];

    /// <summary>Budget and shape of the <c>&lt;skills&gt;</c> block injected into every turn.</summary>
    public SkillCatalogueOptions Catalogue { get; set; } = new();

    /// <summary>Defaults for <c>skills__search</c>.</summary>
    public SkillSearchOptions Search { get; set; } = new();

    /// <summary>Register the <c>skills</c> tool source (<c>skills__load</c>, <c>skills__search</c>).</summary>
    public bool ExposeTools { get; set; } = true;

    /// <summary>Run the file → store sync in <c>IHostedLifecycleService.StartingAsync</c>. False leaves the store as it is (a host that syncs elsewhere).</summary>
    public bool SyncOnStartup { get; set; } = true;
}

/// <summary>Catalogue budget.</summary>
public sealed class SkillCatalogueOptions
{
    /// <summary>Character budget for the whole rendered block; 0 or negative = no budget. On overflow the block ends with an explicit "… and N more" line — truncation is never silent.</summary>
    public int MaxChars { get; set; } = 2000;
}

/// <summary>
/// What <c>skills__search</c> asks the index for. Bindable (a class with setters) because it is part of <see cref="SkillOptions"/>,
/// and therefore a bound singleton: never mutate it per call — copy it.
/// </summary>
public sealed class SkillSearchOptions
{
    /// <summary>Max hits per search; values below 1 are treated as 1.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Minimum similarity in [0, 1] for a hit to be returned.</summary>
    public double MinScore { get; set; } = 0.3;
}
