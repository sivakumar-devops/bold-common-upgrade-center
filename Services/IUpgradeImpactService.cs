namespace Bold.UpgradeCenter.Services;

public interface IUpgradeImpactService
{
    Task<UpgradeImpactResult> GetImpactAsync(string selectedVersion, CancellationToken cancellationToken = default);
}

public sealed record UpgradeImpactResult(
    string Version,
    bool SchemaImpactKnown,
    bool DatabaseSchemaChangesExpected,
    IReadOnlyList<string> DatabaseTypes,
    IReadOnlyList<string> AffectedTables,
    int AffectedTableCount,
    IReadOnlyList<string> Warnings);
