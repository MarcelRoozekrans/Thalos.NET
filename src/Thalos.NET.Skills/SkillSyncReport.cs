namespace Thalos.Skills;

/// <summary>
/// What one <see cref="SkillSyncService.SyncAsync"/> did. <paramref name="Scanned"/> counts the files that produced a valid
/// document (<c>Scanned == Upserted + Unchanged</c>); <paramref name="Skipped"/> counts files that failed to load and were
/// logged rather than fatal; <paramref name="Deactivated"/> counts skills whose file has disappeared.
/// </summary>
/// <param name="Scanned">Files that produced a valid document.</param>
/// <param name="Upserted">Documents written to the store because they were new or their content hash changed.</param>
/// <param name="Unchanged">Documents skipped by their content hash, so the store was never touched for them.</param>
/// <param name="Skipped">Files that failed to load (or lost a duplicate-name race) and were logged rather than fatal.</param>
/// <param name="Deactivated">Previously active skills whose file has disappeared from every root.</param>
public sealed record SkillSyncReport(int Scanned, int Upserted, int Unchanged, int Skipped, int Deactivated);
