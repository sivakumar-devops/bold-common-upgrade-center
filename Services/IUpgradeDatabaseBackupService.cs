namespace Bold.UpgradeCenter.Services;

public interface IUpgradeDatabaseBackupService
{
    Task<UpgradeDatabaseBackupResult> BackupAffectedTablesAsync(string currentVersion, string selectedVersion, string? jobId = null, CancellationToken cancellationToken = default);

    Task<UpgradeDatabaseRestoreResult> RestoreAffectedTablesAsync(UpgradeDatabaseBackupResult backupResult, string? jobId = null, string? logStage = null, CancellationToken cancellationToken = default);

    Task<BackupCleanupResult> CleanupBackupDatabasesAsync(UpgradeDatabaseBackupResult backupResult, CancellationToken cancellationToken = default);
}
