namespace Bold.UpgradeCenter.Services;

public sealed record UpgradeDatabaseBackupResult(
    bool Succeeded,
    bool BackupSkipped,
    string Message,
    string SelectedVersion,
    string? DatabaseType,
    IReadOnlyList<string> AffectedTables,
    BackupStatus? BackupStatus,
    string? BackupDatabaseName,
    string? ScriptPreview,
    string? SourceConnectionString = null,
    string? BackupConnectionString = null,
    IReadOnlyList<DatabaseBackupMapping>? DatabaseBackups = null,
    string? CurrentVersion = null,
    IReadOnlyList<string>? ApplicableVersions = null,
    IReadOnlyList<string>? DatabaseTypesAnalyzed = null,
    IReadOnlyList<string>? ExistingAffectedTables = null,
    IReadOnlyList<string>? CreatedTables = null,
    IReadOnlyList<string>? DroppedTables = null)
{
    public static UpgradeDatabaseBackupResult Failed(string selectedVersion, string message, string? databaseType = null, IReadOnlyList<string>? affectedTables = null)
    {
        return new UpgradeDatabaseBackupResult(false, false, message, selectedVersion, databaseType, affectedTables ?? Array.Empty<string>(), null, null, null, null, null, Array.Empty<DatabaseBackupMapping>());
    }

    public static UpgradeDatabaseBackupResult Skipped(
        string selectedVersion,
        string message,
        string? databaseType = null)
    {
        return new UpgradeDatabaseBackupResult(true, true, message, selectedVersion, databaseType, Array.Empty<string>(), null, null, null, null, null, Array.Empty<DatabaseBackupMapping>());
    }

    public IReadOnlyList<DatabaseBackupMapping> BackupMappings => DatabaseBackups ?? Array.Empty<DatabaseBackupMapping>();

    public IReadOnlyList<string> AnalyzedVersions => ApplicableVersions ?? Array.Empty<string>();

    public IReadOnlyList<string> AnalyzedDatabaseTypes => DatabaseTypesAnalyzed ?? Array.Empty<string>();

    public IReadOnlyList<string> ExistingAffectedTableNames => ExistingAffectedTables ?? Array.Empty<string>();

    public IReadOnlyList<string> CreatedTableNames => CreatedTables ?? Array.Empty<string>();

    public IReadOnlyList<string> DroppedTableNames => DroppedTables ?? Array.Empty<string>();
}

public sealed record DatabaseBackupMapping(
    bool Succeeded,
    string Message,
    string DatabaseType,
    string OriginalDatabaseName,
    string BackupDatabaseName,
    string SourceConnectionString,
    string BackupConnectionString,
    BackupStatus? BackupStatus,
    string? OriginalDatabaseIdentifier = null,
    IReadOnlyList<string>? CreatedTablesToDrop = null,
    IReadOnlyList<string>? TablesToRestore = null)
{
    public IReadOnlyList<string> UpgradeCreatedTablesToDrop => CreatedTablesToDrop ?? Array.Empty<string>();

    public IReadOnlyList<string> BackupTableNames => TablesToRestore ?? Array.Empty<string>();
}

public sealed record UpgradeDatabaseRestoreResult(
    bool Succeeded,
    string Message,
    RestoreStatus? RestoreStatus,
    IReadOnlyList<DatabaseRestoreMapping>? DatabaseRestores = null)
{
    public IReadOnlyList<DatabaseRestoreMapping> RestoreMappings => DatabaseRestores ?? Array.Empty<DatabaseRestoreMapping>();
}

public sealed record DatabaseRestoreMapping(
    bool Succeeded,
    string Message,
    string DatabaseType,
    string OriginalDatabaseName,
    string BackupDatabaseName,
    RestoreStatus? RestoreStatus);

public sealed record BackupCleanupResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<BackupCleanupMapping>? DatabaseCleanups = null)
{
    public IReadOnlyList<BackupCleanupMapping> CleanupMappings => DatabaseCleanups ?? Array.Empty<BackupCleanupMapping>();
}

public sealed record BackupCleanupMapping(
    bool Succeeded,
    string Message,
    string DatabaseType,
    string OriginalDatabaseName,
    string BackupDatabaseName);
