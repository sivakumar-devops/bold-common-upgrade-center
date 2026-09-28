namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeRollbackJobRunner : IUpgradeRollbackJobRunner
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IUpgradeJobStore jobStore;
    private readonly IUpgradeRollbackStore rollbackStore;
    private readonly IUpgradeJobExecutionRegistry executionRegistry;
    private readonly ILogger<UpgradeRollbackJobRunner> logger;

    public UpgradeRollbackJobRunner(
        IServiceScopeFactory scopeFactory,
        IUpgradeJobStore jobStore,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeJobExecutionRegistry executionRegistry,
        ILogger<UpgradeRollbackJobRunner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.jobStore = jobStore;
        this.rollbackStore = rollbackStore;
        this.executionRegistry = executionRegistry;
        this.logger = logger;
    }

    public void Start(string rollbackJobId, string rollbackId)
    {
        QueueRollbackExecution(rollbackJobId, "manual-rollback", () => RunAsync(rollbackJobId, rollbackId, CancellationToken.None));
    }

    public void Recover(string rollbackJobId)
    {
        QueueRollbackExecution(rollbackJobId, "manual-rollback-recovery", () => RecoverAsync(rollbackJobId, CancellationToken.None));
    }

    private void QueueRollbackExecution(string rollbackJobId, string owner, Func<Task> executeAsync)
    {
        if (!executionRegistry.TryBegin(rollbackJobId, owner))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await executeAsync();
            }
            finally
            {
                executionRegistry.Complete(rollbackJobId, owner);
            }
        });
    }

    private async Task RunAsync(string rollbackJobId, string rollbackId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (jobStore.TryGet(rollbackJobId, out var rollbackContextJob))
            {
                scope.ServiceProvider.GetRequiredService<IUpgradeProductContext>().SetCurrent(rollbackContextJob.ProductKey);
            }

            var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
            var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
            var productHealthCheckService = scope.ServiceProvider.GetRequiredService<IProductHealthCheckService>();
            var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();

            if (!rollbackStore.TryGet(rollbackId, out var rollbackEntry))
            {
                await FailRollbackJobAsync(
                    rollbackJobId,
                    rollbackId,
                    UpgradeJobStageName.RollbackComplete,
                    "Rollback failed because the selected rollback point is no longer available.",
                    cancellationToken);
                return;
            }

            jobStore.MarkRunning(rollbackJobId);
            await UpdateHistoryStatusSafelyAsync(historyStore, rollbackJobId, UpgradeJobStatus.Running, cancellationToken);
            await RestoreDatabaseAsync(rollbackJobId, rollbackEntry, databaseBackupService, cancellationToken);

            var databaseSucceeded = rollbackStore.TryGet(rollbackId, out rollbackEntry) &&
                                    rollbackEntry.LastRestoreResult?.Succeeded == true;
            var kubernetesSucceeded = await RestoreKubernetesImagesAsync(rollbackJobId, rollbackEntry, kubernetesUpgradeService, cancellationToken);
            if (rollbackStore.TryGet(rollbackId, out rollbackEntry) &&
                rollbackEntry.LastRestoreResult is not null)
            {
                rollbackStore.MarkRestoreCompleted(
                    rollbackId,
                    rollbackEntry.LastRestoreResult,
                    rollbackEntry.LastKubernetesRollbackResult);
            }

            var selectedDeployments = jobStore.TryGet(rollbackJobId, out var rollbackJobState)
                ? rollbackJobState.SelectedDeployments
                : Array.Empty<string>();
            var healthSucceeded = await VerifyHealthAsync(
                rollbackJobId,
                selectedDeployments,
                kubernetesSucceeded,
                productHealthCheckService,
                cancellationToken);

            var rollbackSucceeded = databaseSucceeded && kubernetesSucceeded && healthSucceeded;
            var message = BuildCompletionMessage(rollbackEntry);
            jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Finalizing rollback state.");
            jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", "Finalizing rollback state.");

            if (rollbackSucceeded)
            {
                await CleanupConsumedRollbackPointAsync(rollbackId, databaseBackupService, cancellationToken);
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", "Rollback completed successfully.");
                jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Rollback completed successfully.");
                jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackSucceeded, "Manual rollback completed.");
                CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackSucceeded, "Manual rollback completed.");
            }
            else
            {
                jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", message);
                jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackComplete, message);
                jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackFailed, message);
                CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackFailed, message);
            }

            await historyStore.UpdateCompletionAsync(
                rollbackJobId,
                rollbackSucceeded ? UpgradeJobStatus.RollbackSucceeded.ToString() : UpgradeJobStatus.RollbackFailed.ToString(),
                DateTimeOffset.UtcNow,
                false,
                rollbackJobId,
                rollbackSucceeded ? null : UpgradeJobStageName.RollbackComplete.ToString(),
                rollbackSucceeded ? null : message,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Manual rollback job failed unexpectedly. RollbackJobId: {RollbackJobId}.", rollbackJobId);
            var message = $"Manual rollback failed unexpectedly: {exception.Message}";
            await FailRollbackJobAsync(rollbackJobId, rollbackId, UpgradeJobStageName.RollbackComplete, message, CancellationToken.None);
        }
    }

    private async Task RecoverAsync(string rollbackJobId, CancellationToken cancellationToken)
    {
        if (!jobStore.TryGet(rollbackJobId, out var rollbackJob))
        {
            logger.LogWarning("Manual rollback recovery skipped because the job was not found. RollbackJobId: {RollbackJobId}.", rollbackJobId);
            return;
        }

        if (IsTerminal(rollbackJob.Status))
        {
            logger.LogInformation(
                "Manual rollback recovery skipped because the job is already terminal. RollbackJobId: {RollbackJobId}. Status: {Status}.",
                rollbackJobId,
                rollbackJob.Status);
            return;
        }

        if (string.IsNullOrWhiteSpace(rollbackJob.RollbackId) ||
            !rollbackStore.TryGet(rollbackJob.RollbackId, out var rollbackEntry))
        {
            await FailRollbackJobAsync(
                rollbackJobId,
                rollbackJob.RollbackId ?? rollbackJobId,
                UpgradeJobStageName.RollbackComplete,
                "Rollback recovery failed because the persisted rollback context is not available.",
                cancellationToken);
            return;
        }

        if (rollbackJob.Status == UpgradeJobStatus.Queued ||
            rollbackJob.CurrentStage == UpgradeJobStageName.Queued)
        {
            logger.LogInformation("Queued manual rollback job recovered and started. RollbackJobId: {RollbackJobId}.", rollbackJobId);
            await RunAsync(rollbackJobId, rollbackEntry.Id, cancellationToken);
            return;
        }

        if (rollbackJob.CurrentStage == UpgradeJobStageName.RollbackDatabaseRestore &&
            !IsStageSucceeded(rollbackJob, UpgradeJobStageName.RollbackDatabaseRestore))
        {
            await FailRollbackJobAsync(
                rollbackJobId,
                rollbackEntry.Id,
                UpgradeJobStageName.RollbackDatabaseRestore,
                "Upgrade Center restarted while restoring database tables. This rollback stage cannot be safely resumed automatically.",
                cancellationToken);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IUpgradeProductContext>().SetCurrent(rollbackJob.ProductKey);
            var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
            var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
            var productHealthCheckService = scope.ServiceProvider.GetRequiredService<IProductHealthCheckService>();
            var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();

            jobStore.MarkRunning(rollbackJobId);
            await UpdateHistoryStatusSafelyAsync(historyStore, rollbackJobId, UpgradeJobStatus.Running, cancellationToken);
            jobStore.MarkStageRunning(
                rollbackJobId,
                UpgradeJobStageName.RollbackRolloutWait,
                "Rollback recovery is rechecking the actual Kubernetes deployment state.");
            jobStore.MarkOperationRunning(
                rollbackJobId,
                UpgradeJobStageName.RollbackRolloutWait,
                "rollback-recovery",
                "Rechecking Kubernetes rollback state after Upgrade Center recovery.");

            var databaseSucceeded = rollbackEntry.LastRestoreResult?.Succeeded == true ||
                                    IsStageSucceeded(rollbackJob, UpgradeJobStageName.RollbackDatabaseRestore);
            var kubernetesSucceeded = await RestoreKubernetesImagesAsync(rollbackJobId, rollbackEntry, kubernetesUpgradeService, cancellationToken);
            if (rollbackStore.TryGet(rollbackEntry.Id, out rollbackEntry) &&
                rollbackEntry.LastRestoreResult is not null)
            {
                rollbackStore.MarkRestoreCompleted(
                    rollbackEntry.Id,
                    rollbackEntry.LastRestoreResult,
                    rollbackEntry.LastKubernetesRollbackResult);
            }

            var selectedDeployments = jobStore.TryGet(rollbackJobId, out var latestRollbackJob)
                ? latestRollbackJob.SelectedDeployments
                : rollbackJob.SelectedDeployments;
            var healthSucceeded = await VerifyHealthAsync(
                rollbackJobId,
                selectedDeployments,
                kubernetesSucceeded,
                productHealthCheckService,
                cancellationToken);

            var rollbackSucceeded = databaseSucceeded && kubernetesSucceeded && healthSucceeded;
            var message = BuildCompletionMessage(rollbackEntry);
            jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Finalizing rollback state.");
            jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", "Finalizing rollback state.");

            if (rollbackSucceeded)
            {
                await CleanupConsumedRollbackPointAsync(rollbackEntry.Id, databaseBackupService, cancellationToken);
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", "Rollback completed successfully after recovery reconciliation.");
                jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Rollback completed successfully after recovery reconciliation.");
                jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackSucceeded, "Manual rollback completed after recovery reconciliation.");
                CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackSucceeded, "Manual rollback completed after recovery reconciliation.");
            }
            else
            {
                jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", message);
                jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackComplete, message);
                jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackFailed, message);
                CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackFailed, message);
            }

            await historyStore.UpdateCompletionAsync(
                rollbackJobId,
                rollbackSucceeded ? UpgradeJobStatus.RollbackSucceeded.ToString() : UpgradeJobStatus.RollbackFailed.ToString(),
                DateTimeOffset.UtcNow,
                false,
                rollbackJobId,
                rollbackSucceeded ? null : UpgradeJobStageName.RollbackComplete.ToString(),
                rollbackSucceeded ? null : message,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Manual rollback recovery failed unexpectedly. RollbackJobId: {RollbackJobId}.", rollbackJobId);
            await FailRollbackJobAsync(
                rollbackJobId,
                rollbackEntry.Id,
                UpgradeJobStageName.RollbackComplete,
                $"Manual rollback recovery failed unexpectedly: {exception.Message}",
                CancellationToken.None);
        }
    }

    private async Task RestoreDatabaseAsync(
        string rollbackJobId,
        UpgradeRollbackEntry rollbackEntry,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "Restoring affected database tables from backup.");
        jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "rollback-database", "Restoring affected database tables from backup.");

        var restoreResult = await databaseBackupService.RestoreAffectedTablesAsync(rollbackEntry.BackupResult, rollbackJobId, "Restore database", cancellationToken);
        if (restoreResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "rollback-database", Safe(restoreResult.Message));
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, Safe(restoreResult.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "rollback-database", Safe(restoreResult.Message));
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, Safe(restoreResult.Message));
        }

        rollbackStore.MarkRestoreProgress(rollbackEntry.Id, restoreResult, null, restoreResult.Succeeded
            ? "Database restore completed. Kubernetes image rollback is pending."
            : "Database restore failed. Kubernetes image rollback will still be attempted.");
    }

    private async Task<bool> RestoreKubernetesImagesAsync(
        string rollbackJobId,
        UpgradeRollbackEntry rollbackEntry,
        IKubernetesUpgradeService kubernetesUpgradeService,
        CancellationToken cancellationToken)
    {
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, "Reverting recorded Kubernetes deployment images.");
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, "Waiting for reverted deployment rollouts.");

        void ReportRollbackProgress(KubernetesDeploymentProgress progress)
        {
            UpdateKubernetesRollbackProgress(rollbackJobId, progress);
        }

        var kubernetesRollbackResult = rollbackEntry.KubernetesResult is null
            ? KubernetesRollbackResult.CreateSkipped("Kubernetes rollback skipped because this rollback point does not contain deployment image mappings.")
            : await kubernetesUpgradeService.RollbackAsync(rollbackEntry.KubernetesResult, cancellationToken, ReportRollbackProgress);

        if (rollbackEntry.KubernetesResult is not null &&
            ShouldReconcileRollback(kubernetesRollbackResult))
        {
            jobStore.MarkStageRunning(
                rollbackJobId,
                UpgradeJobStageName.RollbackRolloutWait,
                "Rollback rollout timed out in Upgrade Center; rechecking actual Kubernetes deployment state.");
            kubernetesRollbackResult = await kubernetesUpgradeService.ReconcileRollbackAsync(
                rollbackEntry.KubernetesResult,
                cancellationToken,
                ReportRollbackProgress);
        }

        if (kubernetesRollbackResult.Skipped)
        {
            jobStore.MarkStageSkipped(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, Safe(kubernetesRollbackResult.Message));
            jobStore.MarkStageSkipped(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, Safe(kubernetesRollbackResult.Message));
        }
        else if (kubernetesRollbackResult.Succeeded)
        {
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, "Deployment image tags were reverted.");
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, Safe(kubernetesRollbackResult.Message));
        }
        else
        {
            CompleteImageRevertStageAfterRollbackFailure(rollbackJobId, Safe(kubernetesRollbackResult.Message));
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, Safe(kubernetesRollbackResult.Message));
        }

        var databaseResult = rollbackStore.TryGet(rollbackEntry.Id, out var latestEntry)
            ? latestEntry.LastRestoreResult
            : null;
        rollbackStore.MarkRestoreProgress(
            rollbackEntry.Id,
            databaseResult,
            kubernetesRollbackResult,
            $"Database rollback: {databaseResult?.Message ?? "Database rollback result is not available yet."}. Kubernetes rollback: {kubernetesRollbackResult.Message}.");

        return kubernetesRollbackResult.Succeeded;
    }

    private static bool ShouldReconcileRollback(KubernetesRollbackResult result)
    {
        return !result.Succeeded &&
               !result.Skipped &&
               result.DeploymentResults.Any(deployment =>
                   deployment.Message.Contains("did not become healthy within", StringComparison.OrdinalIgnoreCase) ||
                   deployment.Message.Contains("Rechecking actual Kubernetes rollout state", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> VerifyHealthAsync(
        string rollbackJobId,
        IReadOnlyCollection<string> selectedDeployments,
        bool kubernetesSucceeded,
        IProductHealthCheckService productHealthCheckService,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "Verifying deployment health after Kubernetes rollout.");

        if (!kubernetesSucceeded)
        {
            jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "rollback-health", "One or more reverted deployments did not report a healthy rollout.");
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "One or more reverted deployments did not report a healthy rollout.");
            return false;
        }

        var targets = productHealthCheckService.GetRollbackHealthCheckTargets(selectedDeployments);
        if (targets.Count > 0)
        {
            jobStore.ReplaceStageOperations(
                rollbackJobId,
                UpgradeJobStageName.RollbackHealthVerification,
                targets.Select(target => CreateHealthOperation(target.OperationId, target.Name)).ToList());
        }
        else
        {
            jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "rollback-health", "No product health endpoints were applicable for the rolled-back deployments.");
        }

        void ReportHealthProgress(ProductHealthCheckProgress progress)
        {
            var message = Safe(progress.Message);
            switch (progress.Status)
            {
                case UpgradeJobStageStatus.Succeeded:
                    jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, progress.Target.OperationId, message);
                    break;
                case UpgradeJobStageStatus.Failed:
                    jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, progress.Target.OperationId, message);
                    break;
                case UpgradeJobStageStatus.Skipped:
                    jobStore.MarkOperationSkipped(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, progress.Target.OperationId, message);
                    break;
                default:
                    jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, progress.Target.OperationId, message);
                    break;
            }
        }

        var result = await productHealthCheckService.VerifyAsync(targets, ReportHealthProgress, cancellationToken);
        if (result.Checks.Count == 0)
        {
            jobStore.ReplaceStageOperations(
                rollbackJobId,
                UpgradeJobStageName.RollbackHealthVerification,
                [CreateHealthOperation("rollback-health", "Verify product health", UpgradeJobStageStatus.Succeeded, Safe(result.Message))]);
        }

        if (result.Succeeded)
        {
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, Safe(result.Message));
        }
        else
        {
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, Safe(result.Message));
        }

        return result.Succeeded;
    }

    private void UpdateKubernetesRollbackProgress(string rollbackJobId, KubernetesDeploymentProgress progress)
    {
        var imageOperationId = InMemoryUpgradeJobStore.KubernetesRollbackImageOperationId(progress.DeploymentName);
        var rolloutOperationId = InMemoryUpgradeJobStore.KubernetesRollbackRolloutOperationId(progress.DeploymentName);
        var message = Safe(progress.Message);

        switch (progress.Status)
        {
            case KubernetesDeploymentProgressStatus.RollingBack:
                jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, imageOperationId, message);
                break;
            case KubernetesDeploymentProgressStatus.RollbackWaitingForRollout:
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, imageOperationId, "Deployment image tag was reverted.");
                jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
            case KubernetesDeploymentProgressStatus.RollbackCompleted:
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, imageOperationId, "Deployment image tag was reverted.");
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
            case KubernetesDeploymentProgressStatus.RollbackFailed:
                jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
            default:
                jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
        }
    }

    private void CompleteImageRevertStageAfterRollbackFailure(string rollbackJobId, string safeMessage)
    {
        if (AllImageRevertOperationsSucceeded(rollbackJobId))
        {
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, "Deployment image tags were reverted.");
            return;
        }

        jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, safeMessage);
    }

    private bool AllImageRevertOperationsSucceeded(string rollbackJobId)
    {
        if (!jobStore.TryGet(rollbackJobId, out var job))
        {
            return false;
        }

        var operations = job.Stages
            .FirstOrDefault(stage => stage.Name == UpgradeJobStageName.RollbackImageRevert)
            ?.Operations;

        return operations is { Count: > 0 } &&
               operations.All(operation => operation.Status == UpgradeJobStageStatus.Succeeded);
    }

    private async Task FailRollbackJobAsync(
        string rollbackJobId,
        string rollbackId,
        UpgradeJobStageName fallbackStageName,
        string message,
        CancellationToken cancellationToken)
    {
        var safeMessage = Safe(message);
        if (jobStore.TryGet(rollbackJobId, out var job))
        {
            var stagesToFail = job.Stages
                .Where(stage => stage.Status == UpgradeJobStageStatus.Running)
                .Select(stage => stage.Name)
                .DefaultIfEmpty(fallbackStageName)
                .Distinct()
                .ToArray();

            foreach (var stageName in stagesToFail)
            {
                jobStore.MarkStageFailed(rollbackJobId, stageName, safeMessage);
            }
        }
        else
        {
            jobStore.MarkStageFailed(rollbackJobId, fallbackStageName, safeMessage);
        }

        jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackFailed, safeMessage);

        if (rollbackStore.TryGet(rollbackId, out var rollbackEntry))
        {
            rollbackStore.MarkRestoreFailed(rollbackId, safeMessage);
            CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackFailed, safeMessage);
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();
            await historyStore.UpdateCompletionAsync(
                rollbackJobId,
                UpgradeJobStatus.RollbackFailed.ToString(),
                DateTimeOffset.UtcNow,
                false,
                rollbackJobId,
                fallbackStageName.ToString(),
                safeMessage,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to update rollback history after rollback failure. RollbackJobId: {RollbackJobId}.", rollbackJobId);
        }
    }

    private async Task UpdateHistoryStatusSafelyAsync(
        IUpgradeHistoryStore historyStore,
        string jobId,
        UpgradeJobStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            await historyStore.UpdateStatusAsync(jobId, status.ToString(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to update Upgrade Center history status. JobId: {JobId}. Status: {Status}.",
                jobId,
                status);
        }
    }

    private void CompleteParentUpgradeJob(UpgradeRollbackEntry rollbackEntry, UpgradeJobStatus status, string message)
    {
        if (string.IsNullOrWhiteSpace(rollbackEntry.UpgradeJobId))
        {
            return;
        }

        if (!jobStore.TryGet(rollbackEntry.UpgradeJobId, out _))
        {
            return;
        }

        jobStore.Complete(rollbackEntry.UpgradeJobId, status, Safe(message));
    }

    private async Task CleanupConsumedRollbackPointAsync(
        string rollbackId,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        if (!rollbackStore.TryGet(rollbackId, out var rollbackEntry))
        {
            return;
        }

        try
        {
            var cleanupResult = await databaseBackupService.CleanupBackupDatabasesAsync(rollbackEntry.BackupResult, cancellationToken);
            rollbackStore.MarkCleanupCompleted(rollbackId, cleanupResult, cleanupResult.Succeeded);
            if (!cleanupResult.Succeeded)
            {
                logger.LogWarning(
                    "Manual rollback backup cleanup did not complete. RollbackId: {RollbackId}. Message: {Message}",
                    rollbackId,
                    Safe(cleanupResult.Message));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Manual rollback backup cleanup failed unexpectedly. RollbackId: {RollbackId}. SafeMessage: {SafeMessage}",
                rollbackId,
                Safe(exception.Message));
            rollbackStore.MarkCleanupCompleted(
                rollbackId,
                new BackupCleanupResult(false, $"Backup cleanup failed unexpectedly: {Safe(exception.Message)}"),
                false);
        }
    }

    private static string BuildCompletionMessage(UpgradeRollbackEntry rollbackEntry)
    {
        return $"Database rollback: {rollbackEntry.LastRestoreResult?.Message ?? "Result unavailable"}. Kubernetes rollback: {rollbackEntry.LastKubernetesRollbackResult?.Message ?? "Result unavailable"}.";
    }

    private static UpgradeJobOperation CreateHealthOperation(
        string id,
        string name,
        UpgradeJobStageStatus status = UpgradeJobStageStatus.Pending,
        string? message = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new UpgradeJobOperation
        {
            Id = id,
            Name = name,
            Status = status,
            StartedAt = status == UpgradeJobStageStatus.Pending ? null : now,
            CompletedAt = status is UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Failed or UpgradeJobStageStatus.Skipped ? now : null,
            Message = message,
            SafeErrorDetails = status == UpgradeJobStageStatus.Failed ? message : null
        };
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "No additional details were returned."
            : value;
    }

    private static bool IsTerminal(UpgradeJobStatus status)
    {
        return status is UpgradeJobStatus.Succeeded
            or UpgradeJobStatus.Failed
            or UpgradeJobStatus.Cancelled
            or UpgradeJobStatus.RollbackSucceeded
            or UpgradeJobStatus.RollbackFailed;
    }

    private static bool IsStageSucceeded(UpgradeJob job, UpgradeJobStageName stageName)
    {
        return job.Stages.Any(stage => stage.Name == stageName && stage.Status == UpgradeJobStageStatus.Succeeded);
    }
}
