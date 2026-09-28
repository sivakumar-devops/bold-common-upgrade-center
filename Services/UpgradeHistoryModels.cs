namespace Bold.UpgradeCenter.Services;

public static class UpgradeHistoryOperationTypes
{
    public const string StandardReleaseUpgrade = "Standard release upgrade";
    public const string CustomVersionOrPatchUpgrade = "Custom version or patch upgrade";
    public const string ManualRollback = "Manual rollback";
    public const string AutomaticRollback = "Automatic rollback";
}

public static class UpgradeHistoryRollbackModes
{
    public const string Manual = "Manual";
    public const string Automatic = "Automatic";
}

public sealed class UpgradeHistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string JobId { get; set; } = string.Empty;

    public string? ParentJobId { get; set; }

    public string Product { get; set; } = "BI";

    public string OperationType { get; set; } = string.Empty;


    public string? PreviousVersion { get; set; }

    public string? TargetVersion { get; set; }

    public string? UpgradeType { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public string InitiatedByUserId { get; set; } = string.Empty;

    public string InitiatedByName { get; set; } = string.Empty;

    public string? InitiatedByEmail { get; set; }

    public string? InitiatedByRole { get; set; }

    public string? RollbackMode { get; set; }

    public string? RollbackJobId { get; set; }

    public bool RollbackAvailable { get; set; }

    public string? FailureStage { get; set; }

    public string? FailureSummary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

public interface IUpgradeHistoryStore
{
    Task SaveStartedAsync(UpgradeHistoryRecord record, CancellationToken cancellationToken = default);

    Task UpdateCompletionAsync(
        string jobId,
        string status,
        DateTimeOffset? completedAt,
        bool rollbackAvailable,
        string? rollbackJobId,
        string? failureStage,
        string? failureSummary,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        string jobId,
        string status,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UpgradeHistoryRecord>> ListAsync(int take = 50, CancellationToken cancellationToken = default);

    Task<UpgradeHistoryRecord?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default);
}
