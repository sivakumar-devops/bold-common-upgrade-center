using System.Text.Json.Serialization;

namespace Bold.UpgradeCenter.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradeJobStatus
{
    Queued,
    Running,
    Cancelling,
    Succeeded,
    Failed,
    Cancelled,
    RollbackSucceeded,
    RollbackFailed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradeJobType
{
    AvailableRelease,
    CustomVersionOrPatch,
    Rollback
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradeJobStageName
{
    Queued,
    PreUpgradeValidation,
    DatabaseBackup,
    KubernetesImageUpgrade,
    PostUpgradeValidation,
    CleanupJob,
    AutomaticRollback,
    RollbackDatabaseRestore,
    RollbackImageRevert,
    RollbackRolloutWait,
    RollbackHealthVerification,
    RollbackComplete
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradeJobStageStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

public sealed class UpgradeJob
{
    public required string Id { get; init; }

    public required string CurrentVersion { get; init; }

    public required string TargetVersion { get; init; }

    public required UpgradeJobType UpgradeType { get; init; }

    public string ProductKey { get; init; } = UpgradeProductKeys.BoldBi;

    public required IReadOnlyList<string> SelectedDeployments { get; init; }

    public IReadOnlyList<CustomPatchImageMapping> CustomImages { get; init; } = [];

    public UpgradeUserInfo InitiatedBy { get; init; } = UpgradeUserInfo.System();

    public UpgradeJobStageName CurrentStage { get; set; } = UpgradeJobStageName.Queued;

    public UpgradeJobStatus Status { get; set; } = UpgradeJobStatus.Queued;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public string? SafeErrorDetails { get; set; }

    public bool CancellationRequested { get; set; }

    public DateTimeOffset? CancellationRequestedAt { get; set; }

    public string? CancellationRequestedBy { get; set; }

    public string? CancellationMessage { get; set; }

    public bool RollbackAvailable { get; set; }

    public string? RollbackId { get; set; }

    public List<UpgradeJobStage> Stages { get; init; } = [];

    public int ProgressPercentage
    {
        get
        {
            var operations = Stages.SelectMany(stage => stage.Operations).ToList();
            if (operations.Count == 0)
            {
                return Status switch
                {
                    UpgradeJobStatus.Succeeded or UpgradeJobStatus.RollbackSucceeded => 100,
                    UpgradeJobStatus.Failed or UpgradeJobStatus.RollbackFailed => 100,
                    UpgradeJobStatus.Running => Stages.Count == 0
                        ? 0
                        : (int)Math.Round(Stages.Count(stage => stage.Status is UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Skipped) * 100.0 / Stages.Count),
                    _ => 0
                };
            }

            var completedOperations = operations.Count(operation =>
                operation.Status is UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Skipped or UpgradeJobStageStatus.Failed or UpgradeJobStageStatus.Cancelled);

            return Math.Clamp((int)Math.Round(completedOperations * 100.0 / operations.Count), 0, 100);
        }
    }
}

public sealed class UpgradeJobStage
{
    public required UpgradeJobStageName Name { get; init; }

    public required string DisplayName { get; init; }

    public UpgradeJobStageStatus Status { get; set; } = UpgradeJobStageStatus.Pending;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Message { get; set; }

    public string? SafeErrorDetails { get; set; }

    public List<UpgradeJobOperation> Operations { get; init; } = [];
}

public sealed class UpgradeJobOperation
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public UpgradeJobStageStatus Status { get; set; } = UpgradeJobStageStatus.Pending;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Message { get; set; }

    public string? SafeErrorDetails { get; set; }
}

public sealed record UpgradeJobCreateResult(
    bool Succeeded,
    UpgradeJob? Job,
    string? ErrorMessage,
    UpgradeJob? ExistingActiveJob = null);
