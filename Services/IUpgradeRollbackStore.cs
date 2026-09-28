namespace Bold.UpgradeCenter.Services;

public interface IUpgradeRollbackStore
{
    UpgradeRollbackEntry Save(
        UpgradeDatabaseBackupResult backupResult,
        KubernetesUpgradeResult? kubernetesResult = null,
        string? upgradeJobId = null,
        UpgradeUserInfo? initiatedBy = null,
        string? previousVersion = null,
        string? targetVersion = null);

    bool TryGet(string id, out UpgradeRollbackEntry entry);

    UpgradeRollbackEntry? GetLatest();

    IReadOnlyList<UpgradeRollbackEntry> MarkSupersededExcept(string activeRollbackId);

    bool TryBeginRestore(string id, out UpgradeRollbackEntry entry, out string message);

    void MarkRestoreProgress(
        string id,
        UpgradeDatabaseRestoreResult? restoreResult = null,
        KubernetesRollbackResult? kubernetesRollbackResult = null,
        string? message = null);

    void MarkRestoreCompleted(string id, UpgradeDatabaseRestoreResult restoreResult, KubernetesRollbackResult? kubernetesRollbackResult = null);

    void MarkRestoreFailed(string id, string message);

    void MarkCleanupCompleted(string id, BackupCleanupResult cleanupResult, bool deleted);
}
