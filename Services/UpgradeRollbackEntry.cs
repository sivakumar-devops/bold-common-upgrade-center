namespace Bold.UpgradeCenter.Services;

public enum UpgradeRollbackState
{
    Available,
    InProgress,
    RolledBack,
    RollbackFailed,
    Consumed,
    Superseded,
    CleanupPending,
    Deleted
}

public sealed class UpgradeRollbackEntry
{
    public required string Id { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required UpgradeDatabaseBackupResult BackupResult { get; init; }

    public KubernetesUpgradeResult? KubernetesResult { get; init; }

    public string? UpgradeJobId { get; init; }

    public string? PreviousVersion { get; init; }

    public string? TargetVersion { get; init; }

    public UpgradeUserInfo InitiatedBy { get; init; } = UpgradeUserInfo.System();

    public UpgradeRollbackState State { get; set; } = UpgradeRollbackState.Available;

    public DateTimeOffset? RestoreStartedAt { get; set; }

    public DateTimeOffset? RestoreCompletedAt { get; set; }

    public string? RestoreMessage { get; set; }

    public DateTimeOffset? LastRestoreAt { get; set; }

    public UpgradeDatabaseRestoreResult? LastRestoreResult { get; set; }

    public KubernetesRollbackResult? LastKubernetesRollbackResult { get; set; }

    public DateTimeOffset? CleanupStartedAt { get; set; }

    public DateTimeOffset? CleanupCompletedAt { get; set; }

    public string? CleanupMessage { get; set; }

    public BackupCleanupResult? LastCleanupResult { get; set; }

    public bool CanExecute => State == UpgradeRollbackState.Available;
}
