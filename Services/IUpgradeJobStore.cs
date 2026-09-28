namespace Bold.UpgradeCenter.Services;

public interface IUpgradeJobStore
{
    UpgradeJob Create(
        string currentVersion,
        string targetVersion,
        UpgradeJobType upgradeType,
        IReadOnlyList<string> selectedDeployments,
        IReadOnlyList<CustomPatchImageMapping>? customImages = null,
        UpgradeUserInfo? initiatedBy = null,
        string? productKey = null);

    UpgradeJob CreateRollback(
        string currentVersion,
        string targetVersion,
        string rollbackId,
        IReadOnlyList<string> selectedDeployments,
        UpgradeUserInfo? initiatedBy = null,
        string? productKey = null);

    bool TryGet(string jobId, out UpgradeJob job);

    bool TryGetLatestActive(out UpgradeJob job);

    bool TryGetLatest(out UpgradeJob job);

    void MarkRunning(string jobId);

    void MarkStageRunning(string jobId, UpgradeJobStageName stageName, string? message = null);

    void MarkStageSucceeded(string jobId, UpgradeJobStageName stageName, string? message = null);

    void MarkStageSkipped(string jobId, UpgradeJobStageName stageName, string? message = null);

    void MarkStageFailed(string jobId, UpgradeJobStageName stageName, string safeErrorDetails);

    void MarkStageCancelled(string jobId, UpgradeJobStageName stageName, string message);

    void ReplaceStageOperations(string jobId, UpgradeJobStageName stageName, IReadOnlyList<UpgradeJobOperation> operations);

    void MarkOperationRunning(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null);

    void MarkOperationSucceeded(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null);

    void MarkOperationSkipped(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null);

    void MarkOperationFailed(string jobId, UpgradeJobStageName stageName, string operationId, string safeErrorDetails);

    void MarkRollbackAvailable(string jobId, string rollbackId);

    bool RequestCancellation(string jobId, string requestedBy, string message);

    void MarkCancelling(string jobId, string message);

    void Complete(string jobId, UpgradeJobStatus status, string? safeErrorDetails = null);
}
