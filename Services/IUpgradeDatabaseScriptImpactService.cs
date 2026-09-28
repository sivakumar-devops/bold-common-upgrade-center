namespace Bold.UpgradeCenter.Services;

public interface IUpgradeDatabaseScriptImpactService
{
    Task<UpgradeDatabaseScriptImpactResult> AnalyzeAsync(
        string currentVersion,
        string targetVersion,
        IReadOnlyList<DatabaseType> databaseTypes,
        CancellationToken cancellationToken = default);
}

public sealed record UpgradeDatabaseScriptImpactResult(
    bool Succeeded,
    string Message,
    string CurrentVersion,
    string TargetVersion,
    IReadOnlyList<string> ApplicableVersions,
    IReadOnlyList<string> DatabaseTypes,
    IReadOnlyList<string> AffectedTables,
    IReadOnlyList<string> ExistingAffectedTables,
    IReadOnlyList<string> CreatedTables,
    IReadOnlyList<string> DroppedTables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AffectedTableVersions,
    IReadOnlyList<UpgradeDatabaseTableImpact> TableImpacts,
    string? ScriptPreview)
{
    public bool HasSchemaChanges => AffectedTables.Count > 0;
}

public sealed record UpgradeDatabaseTableImpact(
    string Component,
    string ChangeType,
    string LogicalTableName,
    string DefaultTablePrefix,
    IReadOnlyList<string> Versions,
    string? SchemaName = null,
    string? SourcePrefix = null,
    string? BaseTableName = null);
