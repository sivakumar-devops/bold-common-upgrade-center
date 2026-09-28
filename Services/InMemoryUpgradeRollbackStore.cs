using System.Collections.Concurrent;

namespace Bold.UpgradeCenter.Services;

public sealed class InMemoryUpgradeRollbackStore : IUpgradeRollbackStore
{
    private readonly ConcurrentDictionary<string, UpgradeRollbackEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUpgradeOperationalStateStore operationalStateStore;

    public InMemoryUpgradeRollbackStore(IUpgradeOperationalStateStore operationalStateStore)
    {
        this.operationalStateStore = operationalStateStore;
    }

    public UpgradeRollbackEntry Save(
        UpgradeDatabaseBackupResult backupResult,
        KubernetesUpgradeResult? kubernetesResult = null,
        string? upgradeJobId = null,
        UpgradeUserInfo? initiatedBy = null,
        string? previousVersion = null,
        string? targetVersion = null)
    {
        var entry = new UpgradeRollbackEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            BackupResult = backupResult,
            KubernetesResult = kubernetesResult,
            UpgradeJobId = upgradeJobId,
            InitiatedBy = initiatedBy ?? UpgradeUserInfo.System(),
            PreviousVersion = previousVersion,
            TargetVersion = targetVersion,
            State = UpgradeRollbackState.Available
        };

        entries[entry.Id] = entry;
        operationalStateStore.SaveRollback(entry);
        return entry;
    }

    public bool TryGet(string id, out UpgradeRollbackEntry entry)
    {
        if (entries.TryGetValue(id, out entry!))
        {
            return true;
        }

        if (operationalStateStore.TryGetRollback(id, out var persistedEntry))
        {
            entries[persistedEntry.Id] = persistedEntry;
            entry = persistedEntry;
            return true;
        }

        entry = null!;
        return false;
    }

    public UpgradeRollbackEntry? GetLatest()
    {
        var latestInMemory = entries.Values
            .OrderByDescending(entry => entry.CreatedAt)
            .FirstOrDefault();
        if (latestInMemory is not null)
        {
            return latestInMemory;
        }

        var latestPersisted = operationalStateStore.GetLatestRollback();
        if (latestPersisted is not null)
        {
            entries[latestPersisted.Id] = latestPersisted;
        }

        return latestPersisted;
    }

    public IReadOnlyList<UpgradeRollbackEntry> MarkSupersededExcept(string activeRollbackId)
    {
        lock (entries)
        {
            LoadPersistedRollbackEntries();
            var superseded = entries.Values
                .Where(entry => !string.Equals(entry.Id, activeRollbackId, StringComparison.OrdinalIgnoreCase) &&
                                entry.State is UpgradeRollbackState.Available or UpgradeRollbackState.CleanupPending)
                .OrderBy(entry => entry.CreatedAt)
                .ToList();

            foreach (var entry in superseded)
            {
                if (entry.State == UpgradeRollbackState.Available)
                {
                    entry.State = UpgradeRollbackState.Superseded;
                }

                entry.CleanupStartedAt = DateTimeOffset.UtcNow;
                entry.CleanupMessage = "Rollback point was superseded by a newer active rollback point. Backup cleanup is pending.";
                entries[entry.Id] = entry;
                operationalStateStore.SaveRollback(entry);
            }

            return superseded;
        }
    }

    public bool TryBeginRestore(string id, out UpgradeRollbackEntry entry, out string message)
    {
        lock (entries)
        {
            if (!TryGet(id, out entry))
            {
                message = "Rollback failed because the selected backup point is no longer available.";
                return false;
            }

            if (entry.State != UpgradeRollbackState.Available)
            {
                message = entry.State switch
                {
                    UpgradeRollbackState.InProgress => "Rollback is already running for this backup point.",
                    UpgradeRollbackState.RolledBack or UpgradeRollbackState.Consumed => "Rollback has already completed for this backup point.",
                    UpgradeRollbackState.Superseded => "Rollback point was superseded by a newer rollback point.",
                    UpgradeRollbackState.CleanupPending => "Rollback point is no longer active and its backup cleanup is pending.",
                    UpgradeRollbackState.Deleted => "Rollback point backup has already been deleted.",
                    UpgradeRollbackState.RollbackFailed => "Rollback already failed for this backup point. Review the failure details before retrying.",
                    _ => "Rollback is not available for this backup point."
                };
                return false;
            }

            entry.State = UpgradeRollbackState.InProgress;
            entry.RestoreStartedAt = DateTimeOffset.UtcNow;
            entry.RestoreMessage = "Rollback started.";
            entries[entry.Id] = entry;
            operationalStateStore.SaveRollback(entry);
            message = "Rollback started.";
            return true;
        }
    }

    public void MarkRestoreCompleted(string id, UpgradeDatabaseRestoreResult restoreResult, KubernetesRollbackResult? kubernetesRollbackResult = null)
    {
        if (!entries.TryGetValue(id, out var entry) &&
            !operationalStateStore.TryGetRollback(id, out entry!))
        {
            return;
        }

        entry.LastRestoreAt = DateTimeOffset.UtcNow;
        entry.LastRestoreResult = restoreResult;
        entry.LastKubernetesRollbackResult = kubernetesRollbackResult;
        entry.RestoreCompletedAt = entry.LastRestoreAt;
        entry.RestoreMessage = $"Database rollback: {restoreResult.Message}. Kubernetes rollback: {kubernetesRollbackResult?.Message ?? "Kubernetes rollback was not required."}.";
        entry.State = restoreResult.Succeeded && kubernetesRollbackResult?.Succeeded != false
            ? UpgradeRollbackState.Consumed
            : UpgradeRollbackState.RollbackFailed;
        entries[entry.Id] = entry;
        operationalStateStore.SaveRollback(entry);
    }

    public void MarkCleanupCompleted(string id, BackupCleanupResult cleanupResult, bool deleted)
    {
        if (!entries.TryGetValue(id, out var entry) &&
            !operationalStateStore.TryGetRollback(id, out entry!))
        {
            return;
        }

        entry.LastCleanupResult = cleanupResult;
        entry.CleanupCompletedAt = DateTimeOffset.UtcNow;
        entry.CleanupMessage = cleanupResult.Message;
        entry.State = deleted ? UpgradeRollbackState.Deleted : UpgradeRollbackState.CleanupPending;
        entries[entry.Id] = entry;
        operationalStateStore.SaveRollback(entry);
    }

    public void MarkRestoreFailed(string id, string message)
    {
        if (!entries.TryGetValue(id, out var entry) &&
            !operationalStateStore.TryGetRollback(id, out entry!))
        {
            return;
        }

        entry.LastRestoreAt = DateTimeOffset.UtcNow;
        entry.RestoreCompletedAt = entry.LastRestoreAt;
        entry.RestoreMessage = message;
        entry.State = UpgradeRollbackState.RollbackFailed;
        entries[entry.Id] = entry;
        operationalStateStore.SaveRollback(entry);
    }

    public void MarkRestoreProgress(
        string id,
        UpgradeDatabaseRestoreResult? restoreResult = null,
        KubernetesRollbackResult? kubernetesRollbackResult = null,
        string? message = null)
    {
        if (!entries.TryGetValue(id, out var entry) &&
            !operationalStateStore.TryGetRollback(id, out entry!))
        {
            return;
        }

        entry.LastRestoreAt = DateTimeOffset.UtcNow;
        entry.LastRestoreResult = restoreResult ?? entry.LastRestoreResult;
        entry.LastKubernetesRollbackResult = kubernetesRollbackResult ?? entry.LastKubernetesRollbackResult;
        entry.RestoreMessage = message ?? entry.RestoreMessage;

        entries[entry.Id] = entry;
        operationalStateStore.SaveRollback(entry);
    }

    private void LoadPersistedRollbackEntries()
    {
        var latestPersisted = operationalStateStore.GetLatestRollback();
        if (latestPersisted is not null)
        {
            entries.TryAdd(latestPersisted.Id, latestPersisted);
        }
    }
}
