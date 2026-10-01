using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeJobRunner : IUpgradeJobRunner
{
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<key>Password|Pwd|User\s*ID|Username|Token|AccessToken|Secret|Key)\s*=\s*[^;,\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IUpgradeJobStore jobStore;
    private readonly IUpgradeJobCancellationManager cancellationManager;
    private readonly ILogger<UpgradeJobRunner> logger;
    private readonly IUpgradeJobExecutionRegistry executionRegistry;

    public UpgradeJobRunner(
        IServiceScopeFactory scopeFactory,
        IUpgradeJobStore jobStore,
        IUpgradeJobCancellationManager cancellationManager,
        IUpgradeJobExecutionRegistry executionRegistry,
        ILogger<UpgradeJobRunner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.jobStore = jobStore;
        this.cancellationManager = cancellationManager;
        this.executionRegistry = executionRegistry;
        this.logger = logger;
    }

    public void Start(string jobId)
    {
        QueueJobExecution(jobId, RunAsync, "start");
    }

    public void Recover(string jobId)
    {
        QueueJobExecution(jobId, RunRecoveryAsync, "recovery");
    }

    private void QueueJobExecution(
        string jobId,
        Func<string, CancellationToken, Task> executeAsync,
        string operationName)
    {
        var owner = $"upgrade-{operationName}";
        if (!executionRegistry.TryBegin(jobId, owner))
        {
            return;
        }

        var cancellationToken = cancellationManager.Register(jobId);
        _ = Task.Run(async () =>
        {
            try
            {
                await executeAsync(jobId, cancellationToken);
            }
            finally
            {
                executionRegistry.Complete(jobId, owner);
            }
        });
    }

    private async Task RunAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!jobStore.TryGet(jobId, out var job))
        {
            logger.LogWarning("Upgrade job {JobId} could not be started because it was not found.", jobId);
            return;
        }

        jobStore.MarkRunning(jobId);
        logger.LogInformation("Upgrade job started. JobId: {JobId}. TargetVersion: {TargetVersion}.", job.Id, job.TargetVersion);

        UpgradeDatabaseBackupResult? backupResult = null;
        KubernetesUpgradeResult? completedKubernetesResult = null;
        UpgradeRollbackEntry? preparedRollbackEntry = null;
        UpgradeRollbackEntry? rollbackEntry = null;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var product = scope.ServiceProvider.GetRequiredService<IUpgradeProductContext>().SetCurrent(job.ProductKey);
            var playwrightRunner = scope.ServiceProvider.GetRequiredService<IPlaywrightScriptRunner>();
            var runnerImageProvider = scope.ServiceProvider.GetRequiredService<IPlaywrightRunnerImageProvider>();
            var playwrightOptions = scope.ServiceProvider.GetRequiredService<IOptions<PlaywrightExecutionOptions>>().Value;
            var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
            var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
            var rollbackStore = scope.ServiceProvider.GetRequiredService<IUpgradeRollbackStore>();
            var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();
            var isCustomPatch = job.UpgradeType == UpgradeJobType.CustomVersionOrPatch;
            await UpdateHistoryStatusSafelyAsync(historyStore, jobId, UpgradeJobStatus.Running, cancellationToken);

            if (isCustomPatch)
            {
                backupResult = UpgradeDatabaseBackupResult.Skipped(
                    job.TargetVersion,
                    "Schema backup skipped for custom patch upgrades.") with
                {
                    CurrentVersion = job.CurrentVersion,
                    ApplicableVersions = Array.Empty<string>(),
                    DatabaseTypesAnalyzed = Array.Empty<string>(),
                    ExistingAffectedTables = Array.Empty<string>(),
                    CreatedTables = Array.Empty<string>(),
                    DroppedTables = Array.Empty<string>()
                };
                jobStore.ReplaceStageOperations(jobId, UpgradeJobStageName.DatabaseBackup, BuildDatabaseBackupOperations(backupResult));
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.DatabaseBackup, Safe(backupResult.Message));
            }
            else
            {
                jobStore.MarkStageRunning(jobId, UpgradeJobStageName.DatabaseBackup, "Backing up affected database tables.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "database-discovery", "Discovering master and tenant databases.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "database-impact", "Analyzing database scripts for the selected upgrade range.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "table-discovery", "Identifying cumulative affected tables.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "database-backup", "Backing up affected database tables.");
                backupResult = await databaseBackupService.BackupAffectedTablesAsync(job.CurrentVersion, job.TargetVersion, jobId, cancellationToken);
                jobStore.ReplaceStageOperations(jobId, UpgradeJobStageName.DatabaseBackup, BuildDatabaseBackupOperations(backupResult));
                if (!backupResult.Succeeded)
                {
                    await CleanupFailedNewBackupAsync(backupResult, databaseBackupService, cancellationToken);
                    FailStageAndJob(jobId, UpgradeJobStageName.DatabaseBackup, backupResult.Message);
                    await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.DatabaseBackup, backupResult.Message, cancellationToken);
                    return;
                }

                jobStore.MarkStageSucceeded(
                    jobId,
                    UpgradeJobStageName.DatabaseBackup,
                    backupResult.BackupSkipped
                        ? Safe(backupResult.Message)
                        : $"Database backup completed. Version count: {backupResult.AnalyzedVersions.Count}. Backup database count: {backupResult.BackupMappings.Count}. Affected table count: {backupResult.AffectedTables.Count}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            jobStore.MarkStageRunning(jobId, UpgradeJobStageName.PreUpgradeValidation, "Running pre-upgrade validation.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-start", "Starting pre-upgrade validation.");
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-start", "Pre-upgrade validation started.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", "Executing pre-upgrade validation.");
            if (playwrightOptions.UseKubernetesJob && !isCustomPatch)
            {
                var targetRunnerImage = await runnerImageProvider.GetRunnerImageAsync(job.TargetVersion, cancellationToken);
                if (string.IsNullOrWhiteSpace(targetRunnerImage))
                {
                    var message = $"Post-upgrade Playwright runner image is not available from release metadata for target version {job.TargetVersion}.";
                    jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", message);
                    jobStore.MarkStageFailed(jobId, UpgradeJobStageName.PreUpgradeValidation, message);
                    await CleanupFailedNewBackupAsync(backupResult, databaseBackupService, cancellationToken);
                    jobStore.Complete(jobId, UpgradeJobStatus.Failed, message);
                    await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.PreUpgradeValidation, message, cancellationToken);
                    return;
                }
            }

            var preUpgradeResult = await playwrightRunner.ExecutePreUpgradeAsync(
                jobId,
                progress => UpdatePlaywrightProgress(jobId, UpgradeJobStageName.PreUpgradeValidation, progress),
                runnerImageVersion: job.CurrentVersion,
                cancellationToken: cancellationToken);
            if (!preUpgradeResult.Succeeded)
            {
                jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", Safe(preUpgradeResult.Message));
                jobStore.MarkStageFailed(jobId, UpgradeJobStageName.PreUpgradeValidation, Safe(preUpgradeResult.Message));
                if (preUpgradeResult.KubernetesJobCreated)
                {
                    await RunCleanupJobAsync(
                        jobId,
                        playwrightRunner,
                        "Pre-upgrade validation failed. Running Playwright cleanup before stopping the upgrade.",
                        CancellationToken.None,
                        runnerImageVersion: job.CurrentVersion);
                }
                else
                {
                    jobStore.MarkStageSkipped(
                        jobId,
                        UpgradeJobStageName.CleanupJob,
                        "Playwright cleanup skipped because the pre-upgrade validation Job was not created.");
                }

                await CleanupFailedNewBackupAsync(backupResult, databaseBackupService, cancellationToken);
                jobStore.Complete(jobId, UpgradeJobStatus.Failed, Safe(preUpgradeResult.Message));
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.PreUpgradeValidation, preUpgradeResult.Message, cancellationToken);
                return;
            }

            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", Safe(preUpgradeResult.Message));
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-complete", "Pre-upgrade validation completed successfully.");
            jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, Safe(preUpgradeResult.Message));

            cancellationToken.ThrowIfCancellationRequested();
            jobStore.MarkStageRunning(jobId, UpgradeJobStageName.KubernetesImageUpgrade, $"Updating all applicable {product.DisplayName} deployment images.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.KubernetesImageUpgrade, "kubernetes-images", "Retrieving target image details.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.KubernetesImageUpgrade, "kubernetes-deployments", $"Updating applicable {product.DisplayName} deployments.");
            void ReportKubernetesProgress(KubernetesDeploymentProgress progress)
            {
                UpdateKubernetesProgress(jobId, progress);
            }

            async Task PersistPreparedRollbackContextAsync(KubernetesUpgradeResult preparedKubernetesResult)
            {
                if (preparedRollbackEntry is not null ||
                    preparedKubernetesResult.Skipped ||
                    preparedKubernetesResult.PreviousImages.Count == 0)
                {
                    return;
                }

                preparedRollbackEntry = rollbackStore.Save(
                    backupResult,
                    preparedKubernetesResult,
                    jobId,
                    job.InitiatedBy,
                    job.CurrentVersion,
                    job.TargetVersion);
                jobStore.MarkRollbackAvailable(jobId, preparedRollbackEntry.Id);
                await historyStore.UpdateCompletionAsync(
                    jobId,
                    UpgradeJobStatus.Running.ToString(),
                    null,
                    true,
                    preparedRollbackEntry.Id,
                    UpgradeJobStageName.KubernetesImageUpgrade.ToString(),
                    "Kubernetes rollback context was recorded before patching deployment images.",
                    cancellationToken);
                logger.LogInformation(
                    "Kubernetes rollback context recorded before image patching. JobId: {JobId}. RollbackId: {RollbackId}. DeploymentCount: {DeploymentCount}.",
                    jobId,
                    preparedRollbackEntry.Id,
                    preparedKubernetesResult.PreviousImages.Count);
                await CleanupSupersededRollbackPointsAsync(preparedRollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
            }

            var kubernetesResult = isCustomPatch
                ? await kubernetesUpgradeService.UpgradeWithCustomImagesAsync(
                    job.TargetVersion,
                    job.CustomImages,
                    cancellationToken,
                    ReportKubernetesProgress,
                    PersistPreparedRollbackContextAsync)
                : await kubernetesUpgradeService.UpgradeAsync(
                    job.TargetVersion,
                    KubernetesUpgradeScope.Bulk,
                    null,
                    cancellationToken,
                    ReportKubernetesProgress,
                    PersistPreparedRollbackContextAsync);
            completedKubernetesResult = kubernetesResult;
            jobStore.ReplaceStageOperations(jobId, UpgradeJobStageName.KubernetesImageUpgrade, BuildKubernetesUpgradeOperations(kubernetesResult));
            if (!kubernetesResult.Succeeded)
            {
                jobStore.MarkStageFailed(jobId, UpgradeJobStageName.KubernetesImageUpgrade, Safe(kubernetesResult.Message));
                var failedUpgradeRollbackEntry = preparedRollbackEntry;
                if (failedUpgradeRollbackEntry is null &&
                    !kubernetesResult.Skipped &&
                    kubernetesResult.PreviousImages.Count > 0)
                {
                    failedUpgradeRollbackEntry = rollbackStore.Save(
                        backupResult,
                        kubernetesResult,
                        jobId,
                        job.InitiatedBy,
                        job.CurrentVersion,
                        job.TargetVersion);
                    jobStore.MarkRollbackAvailable(jobId, failedUpgradeRollbackEntry.Id);
                    await historyStore.UpdateCompletionAsync(
                        jobId,
                        UpgradeJobStatus.Running.ToString(),
                        null,
                        true,
                        failedUpgradeRollbackEntry.Id,
                        UpgradeJobStageName.KubernetesImageUpgrade.ToString(),
                        Safe(kubernetesResult.Message),
                        cancellationToken);
                    await CleanupSupersededRollbackPointsAsync(failedUpgradeRollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
                }

                if (failedUpgradeRollbackEntry is not null)
                {
                    await RunCleanupJobAsync(
                        jobId,
                        playwrightRunner,
                        "Kubernetes image upgrade failed after pre-upgrade validation. Running Playwright cleanup before automatic rollback.",
                        CancellationToken.None,
                        runnerImageVersion: job.TargetVersion);
                    await RunAutomaticRollbackAsync(
                        job,
                        backupResult,
                        kubernetesResult,
                        failedUpgradeRollbackEntry,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        UpgradeJobStageName.KubernetesImageUpgrade,
                        "Kubernetes image rollout failed. Running automatic rollback.",
                        cancellationToken);
                    return;
                }

                jobStore.Complete(jobId, UpgradeJobStatus.Failed, Safe(kubernetesResult.Message));
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.KubernetesImageUpgrade, kubernetesResult.Message, cancellationToken);
                return;
            }

            jobStore.MarkStageSucceeded(
                jobId,
                UpgradeJobStageName.KubernetesImageUpgrade,
                kubernetesResult.Skipped
                    ? Safe(kubernetesResult.Message)
                    : $"Kubernetes image upgrade completed. Deployment count: {kubernetesResult.DeploymentResults.Count}.");

            if (kubernetesResult.Succeeded && !kubernetesResult.Skipped && kubernetesResult.PreviousImages.Count > 0)
            {
                rollbackEntry = preparedRollbackEntry;
                if (rollbackEntry is null)
                {
                    rollbackEntry = rollbackStore.Save(
                        backupResult,
                        kubernetesResult,
                        jobId,
                        job.InitiatedBy,
                        job.CurrentVersion,
                        job.TargetVersion);
                    jobStore.MarkRollbackAvailable(jobId, rollbackEntry.Id);
                }

                await historyStore.UpdateCompletionAsync(
                    jobId,
                    UpgradeJobStatus.Running.ToString(),
                    null,
                    true,
                    rollbackEntry.Id,
                    null,
                    null,
                    cancellationToken);
                    logger.LogInformation(
                        "Rollback point created for upgrade job. JobId: {JobId}. RollbackId: {RollbackId}.",
                        jobId,
                        rollbackEntry.Id);
                    await CleanupSupersededRollbackPointsAsync(rollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
                }

            if (kubernetesResult.Skipped)
            {
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.PostUpgradeValidation, "Post-upgrade validation skipped because Kubernetes image upgrade was skipped.");
                await RunCleanupJobAsync(
                    jobId,
                    playwrightRunner,
                    "Kubernetes image upgrade was skipped after pre-upgrade validation. Running Playwright cleanup before completing the job.",
                    CancellationToken.None,
                    runnerImageVersion: job.CurrentVersion);
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
                jobStore.Complete(jobId, UpgradeJobStatus.Succeeded);
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Succeeded, rollbackEntry is not null, rollbackEntry?.Id, null, null, cancellationToken);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            jobStore.MarkStageRunning(jobId, UpgradeJobStageName.PostUpgradeValidation, "Running post-upgrade validation.");
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-wait", "Kubernetes image upgrade completed; starting post-upgrade validation.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", "Executing post-upgrade validation.");
            var postUpgradeResult = await playwrightRunner.ExecutePostUpgradeAsync(
                jobId,
                progress => UpdatePlaywrightProgress(jobId, UpgradeJobStageName.PostUpgradeValidation, progress),
                runnerImageVersion: job.TargetVersion,
                cancellationToken: cancellationToken);
            if (postUpgradeResult.Succeeded)
            {
                jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
                jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-complete", "Post-upgrade validation completed successfully.");
                jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
                await RunCleanupJobAsync(
                    jobId,
                    playwrightRunner,
                    "Post-upgrade validation completed. Running Playwright cleanup before completing the upgrade.",
                    CancellationToken.None,
                    runnerImageVersion: job.TargetVersion);
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
                jobStore.Complete(jobId, UpgradeJobStatus.Succeeded);
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Succeeded, rollbackEntry is not null, rollbackEntry?.Id, null, null, cancellationToken);
                return;
            }

            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
            jobStore.MarkStageFailed(jobId, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
            await RunCleanupJobAsync(
                jobId,
                playwrightRunner,
                "Post-upgrade validation failed. Running Playwright cleanup before automatic rollback.",
                CancellationToken.None,
                runnerImageVersion: job.TargetVersion);
            await RunAutomaticRollbackAsync(
                job,
                backupResult,
                kubernetesResult,
                rollbackEntry,
                databaseBackupService,
                kubernetesUpgradeService,
                rollbackStore,
                historyStore,
                UpgradeJobStageName.PostUpgradeValidation,
                "Post-upgrade validation failed. Running automatic rollback.",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await HandleCancellationAsync(jobId, backupResult, preparedRollbackEntry ?? rollbackEntry, completedKubernetesResult, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade job failed unexpectedly. JobId: {JobId}.", jobId);
            FailCurrentJob(jobId, $"Upgrade job failed unexpectedly: {exception.Message}");
        }
        finally
        {
            cancellationManager.Complete(jobId);
        }
    }

    private async Task RunRecoveryAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!jobStore.TryGet(jobId, out var job))
        {
            logger.LogWarning("Upgrade job recovery skipped because job {JobId} was not found.", jobId);
            return;
        }

        if (job.Status is not (UpgradeJobStatus.Queued or UpgradeJobStatus.Running or UpgradeJobStatus.Cancelling))
        {
            logger.LogInformation(
                "Upgrade job recovery skipped because job is already terminal. JobId: {JobId}. Status: {Status}.",
                jobId,
                job.Status);
            return;
        }

        if (job.Status == UpgradeJobStatus.Cancelling)
        {
            try
            {
                await HandleCancellationAsync(jobId, null, null, null, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await FailRecoveredCancellationAsync(jobId, "Recovered upgrade cancellation was canceled.", CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Upgrade cancellation recovery failed unexpectedly. JobId: {JobId}.", jobId);
                await FailRecoveredCancellationAsync(jobId, $"Upgrade cancellation recovery failed unexpectedly: {exception.Message}", CancellationToken.None);
            }
            finally
            {
                cancellationManager.Complete(jobId);
            }

            return;
        }

        if (job.Status == UpgradeJobStatus.Queued)
        {
            logger.LogInformation("Queued upgrade job recovered and started. JobId: {JobId}.", jobId);
            await RunAsync(jobId, cancellationToken);
            return;
        }

        logger.LogWarning(
            "Recovering active upgrade job after Upgrade Center restart. JobId: {JobId}. Stage: {Stage}.",
            jobId,
            job.CurrentStage);

        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<IUpgradeProductContext>().SetCurrent(job.ProductKey);
        var playwrightRunner = scope.ServiceProvider.GetRequiredService<IPlaywrightScriptRunner>();
        var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
        var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
        var rollbackStore = scope.ServiceProvider.GetRequiredService<IUpgradeRollbackStore>();
        var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();
        var rollbackEntry = ResolveRollbackEntry(job, rollbackStore);
        await UpdateHistoryStatusSafelyAsync(historyStore, jobId, UpgradeJobStatus.Running, cancellationToken);

        try
        {
            switch (job.CurrentStage)
            {
                case UpgradeJobStageName.KubernetesImageUpgrade:
                    await RecoverKubernetesImageUpgradeAsync(
                        job,
                        rollbackEntry,
                        playwrightRunner,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                case UpgradeJobStageName.PostUpgradeValidation:
                    await RecoverPostUpgradeValidationAsync(
                        job,
                        rollbackEntry,
                        playwrightRunner,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                case UpgradeJobStageName.CleanupJob:
                    await RecoverCleanupJobAsync(
                        job,
                        rollbackEntry,
                        playwrightRunner,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                case UpgradeJobStageName.AutomaticRollback:
                    await RecoverAutomaticRollbackAsync(
                        job,
                        rollbackEntry,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                default:
                    await CleanupInterruptedPlaywrightSharedStateAsync(job, playwrightRunner, cancellationToken);
                    await FailRecoveredJob(
                        job,
                        historyStore,
                        $"Upgrade Center restarted while job was in stage '{job.CurrentStage}'. This stage cannot be safely resumed automatically.",
                        cancellationToken);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            FailCurrentJob(jobId, "Recovered upgrade job was canceled.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade job recovery failed unexpectedly. JobId: {JobId}.", jobId);
            FailCurrentJob(jobId, $"Upgrade job recovery failed unexpectedly: {exception.Message}");
        }
        finally
        {
            cancellationManager.Complete(jobId);
        }
    }

    private async Task CleanupInterruptedPlaywrightSharedStateAsync(
        UpgradeJob job,
        IPlaywrightScriptRunner playwrightRunner,
        CancellationToken cancellationToken)
    {
        if (job.CurrentStage is not (UpgradeJobStageName.PreUpgradeValidation or UpgradeJobStageName.PostUpgradeValidation))
        {
            return;
        }

        try
        {
            await playwrightRunner.CleanupSharedStateAsync(job.Id, cancellationToken);
            logger.LogInformation(
                "Temporary Playwright shared state cleaned up after interrupted validation recovery. JobId: {JobId}. Stage: {Stage}.",
                job.Id,
                job.CurrentStage);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Temporary Playwright shared state cleanup failed after interrupted validation recovery. JobId: {JobId}. Stage: {Stage}.",
                job.Id,
                job.CurrentStage);
        }
    }

    private async Task RecoverKubernetesImageUpgradeAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        if (rollbackEntry?.KubernetesResult is null)
        {
            await FailRecoveredJob(
                job,
                historyStore,
                "Upgrade Center restarted during Kubernetes image upgrade, but no persisted Kubernetes rollback context was available. Automatic rollback cannot safely continue.",
                cancellationToken);
            return;
        }

        jobStore.MarkStageRunning(job.Id, UpgradeJobStageName.KubernetesImageUpgrade, "Recovering Kubernetes rollout status after Upgrade Center restart.");
        void ReportKubernetesProgress(KubernetesDeploymentProgress progress)
        {
            UpdateKubernetesProgress(job.Id, progress);
        }

        var recoveredKubernetesResult = await kubernetesUpgradeService.ReconcileUpgradeRolloutAsync(
            rollbackEntry.KubernetesResult,
            cancellationToken,
            ReportKubernetesProgress);
        jobStore.ReplaceStageOperations(job.Id, UpgradeJobStageName.KubernetesImageUpgrade, BuildKubernetesUpgradeOperations(recoveredKubernetesResult));

        if (!recoveredKubernetesResult.Succeeded)
        {
            jobStore.MarkStageFailed(job.Id, UpgradeJobStageName.KubernetesImageUpgrade, Safe(recoveredKubernetesResult.Message));
            await historyStore.UpdateCompletionAsync(
                job.Id,
                UpgradeJobStatus.Running.ToString(),
                null,
                true,
                rollbackEntry.Id,
                UpgradeJobStageName.KubernetesImageUpgrade.ToString(),
                Safe(recoveredKubernetesResult.Message),
                cancellationToken);
            await RunAutomaticRollbackAsync(
                job,
                rollbackEntry.BackupResult,
                recoveredKubernetesResult,
                rollbackEntry,
                databaseBackupService,
                kubernetesUpgradeService,
                rollbackStore,
                historyStore,
                UpgradeJobStageName.KubernetesImageUpgrade,
                "Recovered Kubernetes rollout failed. Running automatic rollback.",
                cancellationToken);
            return;
        }

        jobStore.MarkStageSucceeded(
            job.Id,
            UpgradeJobStageName.KubernetesImageUpgrade,
            $"Kubernetes rollout recovery completed. Deployment count: {recoveredKubernetesResult.DeploymentResults.Count}.");
        await RunPostUpgradeValidationAfterKubernetesRecoveryAsync(
            job,
            rollbackEntry.BackupResult,
            recoveredKubernetesResult,
            rollbackEntry,
            playwrightRunner,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            cancellationToken);
    }

    private async Task RecoverPostUpgradeValidationAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        if (rollbackEntry?.KubernetesResult is null)
        {
            await FailRecoveredJob(
                job,
                historyStore,
                "Upgrade Center restarted during post-upgrade validation, but no persisted rollback context was available. Automatic rollback cannot safely continue.",
                cancellationToken);
            return;
        }

        await RunPostUpgradeValidationAfterKubernetesRecoveryAsync(
            job,
            rollbackEntry.BackupResult,
            rollbackEntry.KubernetesResult,
            rollbackEntry,
            playwrightRunner,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            cancellationToken);
    }

    private async Task RecoverCleanupJobAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        await RunCleanupJobAsync(
            job.Id,
            playwrightRunner,
            "Recovering Playwright cleanup after Upgrade Center restart.",
            CancellationToken.None,
            runnerImageVersion: ResolveCleanupRunnerImageVersion(job),
            rerunIfAlreadyRunning: true);

        if (!jobStore.TryGet(job.Id, out var latestJob))
        {
            return;
        }

        var preStage = latestJob.Stages.FirstOrDefault(stage => stage.Name == UpgradeJobStageName.PreUpgradeValidation);
        var kubernetesStage = latestJob.Stages.FirstOrDefault(stage => stage.Name == UpgradeJobStageName.KubernetesImageUpgrade);
        var postStage = latestJob.Stages.FirstOrDefault(stage => stage.Name == UpgradeJobStageName.PostUpgradeValidation);

        if (postStage?.Status == UpgradeJobStageStatus.Succeeded)
        {
            jobStore.MarkStageSkipped(job.Id, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
            jobStore.Complete(job.Id, UpgradeJobStatus.Succeeded);
            await UpdateHistoryCompletionAsync(historyStore, job.Id, UpgradeJobStatus.Succeeded, rollbackEntry is not null, rollbackEntry?.Id, null, null, cancellationToken);
            return;
        }

        if (postStage?.Status == UpgradeJobStageStatus.Failed &&
            rollbackEntry?.KubernetesResult is not null)
        {
            await RunAutomaticRollbackAsync(
                latestJob,
                rollbackEntry.BackupResult,
                rollbackEntry.KubernetesResult,
                rollbackEntry,
                databaseBackupService,
                kubernetesUpgradeService,
                rollbackStore,
                historyStore,
                UpgradeJobStageName.PostUpgradeValidation,
                "Post-upgrade validation failed before cleanup recovery. Running automatic rollback.",
                cancellationToken);
            return;
        }

        if (kubernetesStage?.Status == UpgradeJobStageStatus.Failed &&
            rollbackEntry?.KubernetesResult is not null)
        {
            await RunAutomaticRollbackAsync(
                latestJob,
                rollbackEntry.BackupResult,
                rollbackEntry.KubernetesResult,
                rollbackEntry,
                databaseBackupService,
                kubernetesUpgradeService,
                rollbackStore,
                historyStore,
                UpgradeJobStageName.KubernetesImageUpgrade,
                "Kubernetes image upgrade failed before cleanup recovery. Running automatic rollback.",
                cancellationToken);
            return;
        }

        if (preStage?.Status == UpgradeJobStageStatus.Failed)
        {
            var message = preStage.SafeErrorDetails ?? preStage.Message ?? "Pre-upgrade validation failed.";
            jobStore.Complete(job.Id, UpgradeJobStatus.Failed, Safe(message));
            await UpdateHistoryCompletionAsync(historyStore, job.Id, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.PreUpgradeValidation, message, cancellationToken);
            return;
        }

        await FailRecoveredJob(
            latestJob,
            historyStore,
            "Upgrade Center restarted during Cleanup Job, but the next workflow action could not be determined from persisted stage state.",
            cancellationToken);
    }

    private async Task RecoverAutomaticRollbackAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        if (rollbackEntry?.KubernetesResult is null)
        {
            await FailRecoveredJob(
                job,
                historyStore,
                "Upgrade Center restarted during automatic rollback, but no persisted rollback context was available.",
                cancellationToken);
            return;
        }

        var alreadyStarted = rollbackEntry.State == UpgradeRollbackState.InProgress;
        await RunAutomaticRollbackAsync(
            job,
            rollbackEntry.BackupResult,
            rollbackEntry.KubernetesResult,
            rollbackEntry,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            UpgradeJobStageName.AutomaticRollback,
            "Recovering automatic rollback after Upgrade Center restart.",
            cancellationToken,
            alreadyStarted);
    }

    private async Task RunPostUpgradeValidationAfterKubernetesRecoveryAsync(
        UpgradeJob job,
        UpgradeDatabaseBackupResult backupResult,
        KubernetesUpgradeResult kubernetesResult,
        UpgradeRollbackEntry rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        jobStore.MarkStageRunning(job.Id, UpgradeJobStageName.PostUpgradeValidation, "Running post-upgrade validation after recovery.");
        jobStore.MarkOperationSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-wait", "Kubernetes rollout recovery completed; starting post-upgrade validation.");
        jobStore.MarkOperationRunning(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", "Executing post-upgrade validation.");

        var postUpgradeResult = await playwrightRunner.ExecutePostUpgradeAsync(
            job.Id,
            progress => UpdatePlaywrightProgress(job.Id, UpgradeJobStageName.PostUpgradeValidation, progress),
            runnerImageVersion: job.TargetVersion,
            cancellationToken: cancellationToken);
        if (postUpgradeResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
            jobStore.MarkOperationSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-complete", "Post-upgrade validation completed successfully.");
            jobStore.MarkStageSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
            await RunCleanupJobAsync(
                job.Id,
                playwrightRunner,
                "Recovered post-upgrade validation completed. Running Playwright cleanup before completing the upgrade.",
                CancellationToken.None,
                runnerImageVersion: job.TargetVersion);
            jobStore.MarkStageSkipped(job.Id, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
            jobStore.Complete(job.Id, UpgradeJobStatus.Succeeded);
            await UpdateHistoryCompletionAsync(historyStore, job.Id, UpgradeJobStatus.Succeeded, true, rollbackEntry.Id, null, null, cancellationToken);
            return;
        }

        jobStore.MarkOperationFailed(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
        jobStore.MarkStageFailed(job.Id, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
        await RunCleanupJobAsync(
            job.Id,
            playwrightRunner,
            "Recovered post-upgrade validation failed. Running Playwright cleanup before automatic rollback.",
            CancellationToken.None,
            runnerImageVersion: job.TargetVersion);
        await RunAutomaticRollbackAsync(
            job,
            backupResult,
            kubernetesResult,
            rollbackEntry,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            UpgradeJobStageName.PostUpgradeValidation,
            "Post-upgrade validation failed after recovery. Running automatic rollback.",
            cancellationToken);
    }

    private async Task RunAutomaticRollbackAsync(
        UpgradeJob job,
        UpgradeDatabaseBackupResult backupResult,
        KubernetesUpgradeResult kubernetesResult,
        UpgradeRollbackEntry? rollbackEntry,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        UpgradeJobStageName failureStage,
        string failureSummary,
        CancellationToken cancellationToken,
        bool rollbackAlreadyStarted = false)
    {
        var jobId = job.Id;
        FinalizeInterruptedStageBeforeAutomaticRollback(jobId, failureStage, failureSummary);
        jobStore.MarkStageRunning(jobId, UpgradeJobStageName.AutomaticRollback, failureSummary);
        jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", "Restoring affected database backups.");
        if (!rollbackAlreadyStarted &&
            rollbackEntry is not null &&
            !rollbackStore.TryBeginRestore(rollbackEntry.Id, out rollbackEntry, out var beginRollbackMessage))
        {
            var safeBeginRollbackMessage = Safe(beginRollbackMessage);
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", safeBeginRollbackMessage);
            jobStore.MarkStageFailed(jobId, UpgradeJobStageName.AutomaticRollback, safeBeginRollbackMessage);
            jobStore.Complete(jobId, UpgradeJobStatus.RollbackFailed, safeBeginRollbackMessage);
            await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.RollbackFailed, false, rollbackEntry?.Id, UpgradeJobStageName.AutomaticRollback, safeBeginRollbackMessage, cancellationToken);
            return;
        }

        var rollbackHistoryJobId = $"auto-rollback-{Guid.NewGuid():N}";
        await historyStore.SaveStartedAsync(new UpgradeHistoryRecord
        {
            JobId = rollbackHistoryJobId,
            ParentJobId = jobId,
            Product = UpgradeProductDefinitions.Resolve(job.ProductKey).Key,
            OperationType = UpgradeHistoryOperationTypes.AutomaticRollback,
            PreviousVersion = job.TargetVersion,
            TargetVersion = job.CurrentVersion,
            UpgradeType = "Rollback",
            Status = UpgradeJobStatus.Running.ToString(),
            StartedAt = DateTimeOffset.UtcNow,
            InitiatedByUserId = job.InitiatedBy.UserId,
            InitiatedByName = job.InitiatedBy.DisplayName,
            InitiatedByEmail = job.InitiatedBy.Email,
            InitiatedByRole = job.InitiatedBy.Role,
            RollbackMode = UpgradeHistoryRollbackModes.Automatic,
            RollbackJobId = rollbackHistoryJobId,
            FailureStage = failureStage.ToString(),
            FailureSummary = failureSummary
        }, cancellationToken);

        var databaseRollbackResult = await databaseBackupService.RestoreAffectedTablesAsync(backupResult, jobId, "Automatic rollback", cancellationToken);
        if (databaseRollbackResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", Safe(databaseRollbackResult.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", Safe(databaseRollbackResult.Message));
        }

        jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-kubernetes", "Restoring previous Kubernetes deployment images.");
        void ReportRollbackProgress(KubernetesDeploymentProgress progress)
        {
            UpdateKubernetesProgress(jobId, progress);
        }

        var kubernetesRollbackResult = await kubernetesUpgradeService.RollbackAsync(kubernetesResult, cancellationToken, ReportRollbackProgress);
        if (kubernetesRollbackResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-kubernetes", Safe(kubernetesRollbackResult.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-kubernetes", Safe(kubernetesRollbackResult.Message));
        }

        var productHealthSucceeded = await VerifyProductHealthAfterAutomaticRollbackAsync(
            jobId,
            kubernetesRollbackResult.Succeeded,
            kubernetesResult.PreviousImages.Select(snapshot => snapshot.DeploymentName).ToList(),
            cancellationToken);

        if (rollbackEntry is not null)
        {
            rollbackStore.MarkRestoreCompleted(rollbackEntry.Id, databaseRollbackResult, kubernetesRollbackResult);
        }

        var rollbackSucceeded = databaseRollbackResult.Succeeded && kubernetesRollbackResult.Succeeded && productHealthSucceeded;
        var message = $"Database rollback: {databaseRollbackResult.Message}. Kubernetes rollback: {kubernetesRollbackResult.Message}.";
        if (rollbackSucceeded)
        {
            if (rollbackEntry is not null)
            {
                await CleanupConsumedRollbackPointAsync(rollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
            }

            jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, Safe(message));
            jobStore.Complete(jobId, UpgradeJobStatus.RollbackSucceeded, "Automatic rollback completed.");
            await historyStore.UpdateCompletionAsync(rollbackHistoryJobId, UpgradeJobStatus.RollbackSucceeded.ToString(), DateTimeOffset.UtcNow, false, rollbackHistoryJobId, null, null, cancellationToken);
            await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.RollbackSucceeded, false, rollbackEntry?.Id, failureStage, "Automatic rollback completed.", cancellationToken);
            return;
        }

        jobStore.MarkStageFailed(jobId, UpgradeJobStageName.AutomaticRollback, Safe(message));
        jobStore.Complete(jobId, UpgradeJobStatus.RollbackFailed, "Automatic rollback did not complete successfully.");
        await historyStore.UpdateCompletionAsync(rollbackHistoryJobId, UpgradeJobStatus.RollbackFailed.ToString(), DateTimeOffset.UtcNow, false, rollbackHistoryJobId, UpgradeJobStageName.AutomaticRollback.ToString(), message, cancellationToken);
        await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.RollbackFailed, false, rollbackEntry?.Id, UpgradeJobStageName.AutomaticRollback, message, cancellationToken);
    }

    private async Task<bool> VerifyProductHealthAfterAutomaticRollbackAsync(
        string jobId,
        bool kubernetesSucceeded,
        IReadOnlyCollection<string> deploymentNames,
        CancellationToken cancellationToken)
    {
        if (!kubernetesSucceeded)
        {
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-health", "Product health verification skipped because Kubernetes rollback did not complete successfully.");
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        if (jobStore.TryGet(jobId, out var healthJob))
        {
            scope.ServiceProvider.GetRequiredService<IUpgradeProductContext>().SetCurrent(healthJob.ProductKey);
        }

        var productHealthCheckService = scope.ServiceProvider.GetRequiredService<IProductHealthCheckService>();
        var targets = productHealthCheckService.GetRollbackHealthCheckTargets(deploymentNames);
        jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-health", "Verifying product health endpoints after automatic rollback.");

        void ReportHealthProgress(ProductHealthCheckProgress progress)
        {
            var message = $"{progress.Target.Name}: {Safe(progress.Message)}";
            switch (progress.Status)
            {
                case UpgradeJobStageStatus.Succeeded:
                    jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, progress.Target.OperationId, message);
                    break;
                case UpgradeJobStageStatus.Failed:
                    jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, progress.Target.OperationId, message);
                    break;
                case UpgradeJobStageStatus.Skipped:
                    jobStore.MarkOperationSkipped(jobId, UpgradeJobStageName.AutomaticRollback, progress.Target.OperationId, message);
                    break;
                default:
                    jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.AutomaticRollback, progress.Target.OperationId, message);
                    break;
            }
        }

        var result = await productHealthCheckService.VerifyAsync(targets, ReportHealthProgress, cancellationToken);
        if (result.Succeeded)
        {
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-health", Safe(result.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-health", Safe(result.Message));
        }

        return result.Succeeded;
    }

    private async Task FailRecoveredJob(
        UpgradeJob job,
        IUpgradeHistoryStore historyStore,
        string message,
        CancellationToken cancellationToken)
    {
        var safeMessage = Safe(message);
        jobStore.MarkStageFailed(job.Id, job.CurrentStage, safeMessage);
        jobStore.Complete(job.Id, UpgradeJobStatus.Failed, safeMessage);
        await UpdateHistoryCompletionAsync(historyStore, job.Id, UpgradeJobStatus.Failed, false, job.RollbackId, job.CurrentStage, safeMessage, cancellationToken);
    }

    private void FinalizeInterruptedStageBeforeAutomaticRollback(
        string jobId,
        UpgradeJobStageName failureStage,
        string failureSummary)
    {
        if (failureStage == UpgradeJobStageName.AutomaticRollback ||
            !jobStore.TryGet(jobId, out var latestJob))
        {
            return;
        }

        var interruptedStage = latestJob.Stages.FirstOrDefault(stage => stage.Name == failureStage);
        if (interruptedStage is null ||
            interruptedStage.Status is not (UpgradeJobStageStatus.Running or UpgradeJobStageStatus.Pending))
        {
            return;
        }

        var isCancellationRollback = latestJob.CancellationRequested || latestJob.Status == UpgradeJobStatus.Cancelling;
        if (isCancellationRollback)
        {
            jobStore.MarkStageCancelled(
                jobId,
                failureStage,
                "Stage was interrupted by cancellation. Automatic rollback started.");
            return;
        }

        jobStore.MarkStageFailed(jobId, failureStage, Safe(failureSummary));
    }

    private async Task FailRecoveredCancellationAsync(
        string jobId,
        string message,
        CancellationToken cancellationToken)
    {
        var safeMessage = Safe(message);
        UpgradeJobStageName? failureStage = null;
        string? rollbackId = null;
        if (jobStore.TryGet(jobId, out var job))
        {
            failureStage = job.CurrentStage;
            rollbackId = job.RollbackId;
            jobStore.MarkStageFailed(jobId, job.CurrentStage, safeMessage);
        }

        jobStore.Complete(jobId, UpgradeJobStatus.Failed, safeMessage);

        using var scope = scopeFactory.CreateScope();
        var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();
        await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, rollbackId, failureStage, safeMessage, cancellationToken);
    }

    private async Task HandleCancellationAsync(
        string jobId,
        UpgradeDatabaseBackupResult? backupResult,
        UpgradeRollbackEntry? rollbackEntry,
        KubernetesUpgradeResult? kubernetesResult,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        if (jobStore.TryGet(jobId, out var cancellationJob))
        {
            scope.ServiceProvider.GetRequiredService<IUpgradeProductContext>().SetCurrent(cancellationJob.ProductKey);
        }

        var playwrightRunner = scope.ServiceProvider.GetRequiredService<IPlaywrightScriptRunner>();
        var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
        var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
        var rollbackStore = scope.ServiceProvider.GetRequiredService<IUpgradeRollbackStore>();
        var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();

        if (!jobStore.TryGet(jobId, out var job))
        {
            return;
        }

        var safeMessage = Safe(job.CancellationMessage ?? "Upgrade cancellation requested.");
        jobStore.MarkCancelling(jobId, safeMessage);

        switch (job.CurrentStage)
        {
            case UpgradeJobStageName.KubernetesImageUpgrade:
            case UpgradeJobStageName.PostUpgradeValidation:
                if (ShouldRunPlaywrightCleanup(job))
                {
                    await RunCleanupJobAsync(
                        jobId,
                        playwrightRunner,
                        "Upgrade cancellation requested after Playwright validation started. Running Playwright cleanup.",
                        CancellationToken.None,
                        runnerImageVersion: ResolveCleanupRunnerImageVersion(job));
                }

                rollbackEntry ??= ResolveRollbackEntry(job, rollbackStore);
                kubernetesResult ??= rollbackEntry?.KubernetesResult;
                backupResult ??= rollbackEntry?.BackupResult;
                if (rollbackEntry is not null && backupResult is not null && kubernetesResult is not null)
                {
                    await RunAutomaticRollbackAsync(
                        job,
                        backupResult,
                        kubernetesResult,
                        rollbackEntry,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        job.CurrentStage,
                        "Upgrade cancellation requested after deployment image changes. Running automatic rollback.",
                        cancellationToken);
                    return;
                }

                jobStore.MarkStageCancelled(jobId, job.CurrentStage, "Upgrade cancellation completed before deployment image changes were confirmed.");
                jobStore.Complete(jobId, UpgradeJobStatus.Cancelled, "Upgrade cancelled. No rollback context was available or required.");
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Cancelled, false, job.RollbackId, null, "Upgrade cancelled.", cancellationToken);
                return;

            case UpgradeJobStageName.PreUpgradeValidation:
                await RunCleanupJobAsync(
                    jobId,
                    playwrightRunner,
                    "Upgrade cancellation requested during pre-upgrade validation. Running Playwright cleanup.",
                    CancellationToken.None,
                    runnerImageVersion: job.CurrentVersion);
                if (backupResult is not null)
                {
                    await CleanupFailedNewBackupAsync(backupResult, databaseBackupService, cancellationToken);
                }

                jobStore.MarkStageCancelled(jobId, UpgradeJobStageName.PreUpgradeValidation, "Pre-upgrade validation was cancelled by the administrator.");
                jobStore.Complete(jobId, UpgradeJobStatus.Cancelled, "Upgrade cancelled before deployment images were changed.");
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Cancelled, false, null, null, "Upgrade cancelled before deployment images were changed.", cancellationToken);
                return;

            case UpgradeJobStageName.DatabaseBackup:
            case UpgradeJobStageName.Queued:
            default:
                if (backupResult is not null)
                {
                    await CleanupFailedNewBackupAsync(backupResult, databaseBackupService, cancellationToken);
                }

                if (job.CurrentStage != UpgradeJobStageName.Queued)
                {
                    jobStore.MarkStageCancelled(jobId, job.CurrentStage, "Upgrade was cancelled by the administrator.");
                }

                jobStore.Complete(jobId, UpgradeJobStatus.Cancelled, "Upgrade cancelled before deployment images were changed.");
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Cancelled, false, null, null, "Upgrade cancelled before deployment images were changed.", cancellationToken);
                return;
        }
    }

    private static string ResolveCleanupRunnerImageVersion(UpgradeJob job)
    {
        var kubernetesStage = job.Stages.FirstOrDefault(stage => stage.Name == UpgradeJobStageName.KubernetesImageUpgrade);
        var postStage = job.Stages.FirstOrDefault(stage => stage.Name == UpgradeJobStageName.PostUpgradeValidation);

        if (postStage?.Status is UpgradeJobStageStatus.Running or UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Failed)
        {
            return job.TargetVersion;
        }

        if (kubernetesStage?.Status is UpgradeJobStageStatus.Running or UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Failed)
        {
            return job.TargetVersion;
        }

        return job.CurrentVersion;
    }

    private async Task<PlaywrightScriptResult?> RunCleanupJobAsync(
        string jobId,
        IPlaywrightScriptRunner playwrightRunner,
        string reason,
        CancellationToken cancellationToken,
        string? runnerImageVersion = null,
        bool rerunIfAlreadyRunning = false)
    {
        if (!jobStore.TryGet(jobId, out var latestJob))
        {
            return null;
        }

        var cleanupStage = latestJob.Stages.FirstOrDefault(stage => stage.Name == UpgradeJobStageName.CleanupJob);
        if (cleanupStage is null)
        {
            return null;
        }

        if (cleanupStage.Status is UpgradeJobStageStatus.Running && !rerunIfAlreadyRunning)
        {
            logger.LogInformation("Playwright cleanup skipped because cleanup is already running. JobId: {JobId}.", jobId);
            return null;
        }

        if (cleanupStage.Status is UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Failed or UpgradeJobStageStatus.Skipped)
        {
            logger.LogInformation("Playwright cleanup skipped because cleanup already reached a terminal state. JobId: {JobId}. Status: {Status}.", jobId, cleanupStage.Status);
            return null;
        }

        jobStore.MarkStageRunning(jobId, UpgradeJobStageName.CleanupJob, Safe(reason));

        try
        {
            var result = await playwrightRunner.ExecuteCleanupAsync(
                jobId,
                progress => UpdatePlaywrightProgress(jobId, UpgradeJobStageName.CleanupJob, progress),
                runnerImageVersion: runnerImageVersion ?? ResolveCleanupRunnerImageVersion(latestJob),
                cancellationToken: cancellationToken);

            if (IsPlaywrightCleanupSkipped(result))
            {
                var safeMessage = Safe(result.Message);
                jobStore.ReplaceStageOperations(
                    jobId,
                    UpgradeJobStageName.CleanupJob,
                    CreateSkippedCleanupOperations(result, safeMessage));
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.CleanupJob, safeMessage);
                logger.LogInformation("Playwright cleanup skipped for upgrade job {JobId}. Reason: {Reason}", jobId, safeMessage);
                return result;
            }

            if (IsPlaywrightCleanupSuccessful(result))
            {
                jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.CleanupJob, "playwright-tests", BuildCleanupOperationMessage(result, succeeded: true));
                jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.CleanupJob, "Playwright-created tenants, users, and validation resources were cleaned up.");
            }
            else
            {
                jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.CleanupJob, "playwright-tests", BuildCleanupOperationMessage(result, succeeded: false));
                jobStore.MarkStageFailed(jobId, UpgradeJobStageName.CleanupJob, CleanupManualActionMessage);
                logger.LogWarning("Playwright cleanup failed for upgrade job {JobId}. Message: {Message}", jobId, Safe(result.Message));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            jobStore.MarkStageFailed(jobId, UpgradeJobStageName.CleanupJob, CleanupManualActionMessage);
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Playwright cleanup failed unexpectedly: {exception.Message}";
            jobStore.MarkStageFailed(jobId, UpgradeJobStageName.CleanupJob, CleanupManualActionMessage);
            logger.LogWarning(exception, "Playwright cleanup failed unexpectedly. JobId: {JobId}.", jobId);
            return new PlaywrightScriptResult(
                false,
                null,
                false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "Playwright cleanup",
                string.Empty,
                string.Empty,
                Safe(message));
        }
    }

    private const string CleanupManualActionMessage = "Cleanup could not be completed. Manual cleanup is required.";

    private static string BuildCleanupOperationMessage(PlaywrightScriptResult result, bool succeeded)
    {
        var summary = TryParseCleanupSummary(result.StandardOutput)
            ?? TryParseCleanupSummary(result.StandardError)
            ?? TryParseCleanupSummary(result.Message);
        if (summary is null)
        {
            return succeeded
                ? "Cleanup completed successfully. All validation resources were removed."
                : "Cleanup failed. Manual cleanup is required for the remaining resources.";
        }

        return succeeded
            ? $"Cleanup completed successfully. Total: {summary.Total}, Passed: {summary.Passed}, Failed: {summary.Failed}, Skipped: {summary.Skipped}."
            : $"Cleanup failed. Total: {summary.Total}, Passed: {summary.Passed}, Failed: {summary.Failed}, Skipped: {summary.Skipped}. Manual cleanup is required for the remaining resources.";
    }

    private static bool IsPlaywrightCleanupSuccessful(PlaywrightScriptResult result)
    {
        if (!result.Succeeded)
        {
            return false;
        }

        var summary = TryParseCleanupSummary(result.StandardOutput)
            ?? TryParseCleanupSummary(result.StandardError)
            ?? TryParseCleanupSummary(result.Message);
        return summary is not null &&
               summary.Total > 0 &&
               summary.Failed == 0 &&
               summary.Passed + summary.Skipped == summary.Total;
    }

    private static CleanupSummary? TryParseCleanupSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var jsonSummary = TryParseCleanupSummaryJson(text);
        if (jsonSummary is not null)
        {
            return jsonSummary;
        }

        var match = Regex.Match(
            text,
            @"Total\s*[:=]\s*(?<total>\d+).*?Passed\s*[:=]\s*(?<passed>\d+).*?Failed\s*[:=]\s*(?<failed>\d+).*?Skipped\s*[:=]\s*(?<skipped>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            ? new CleanupSummary(
                int.Parse(match.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["passed"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["failed"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["skipped"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private static CleanupSummary? TryParseCleanupSummaryJson(string text)
    {
        const string marker = "PLAYWRIGHT_RESULT_JSON ";
        var markerIndex = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var jsonStart = markerIndex + marker.Length;
        var jsonEnd = text.IndexOfAny(['\r', '\n'], jsonStart);
        var json = jsonEnd >= 0 ? text[jsonStart..jsonEnd] : text[jsonStart..];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new CleanupSummary(
                root.TryGetProperty("total", out var total) ? total.GetInt32() : 0,
                root.TryGetProperty("passed", out var passed) ? passed.GetInt32() : 0,
                root.TryGetProperty("failed", out var failed) ? failed.GetInt32() : 0,
                root.TryGetProperty("skipped", out var skipped) ? skipped.GetInt32() : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CleanupSummary(int Total, int Passed, int Failed, int Skipped);

    private static bool IsPlaywrightCleanupSkipped(PlaywrightScriptResult result)
    {
        return result.Succeeded &&
               result.Message.Contains("cleanup skipped", StringComparison.OrdinalIgnoreCase) &&
               result.Message.Contains("Kubernetes Playwright runner is disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<UpgradeJobOperation> CreateSkippedCleanupOperations(PlaywrightScriptResult result, string safeMessage)
    {
        return
        [
            new UpgradeJobOperation
            {
                Id = "playwright-cleanup-skipped",
                Name = "Skip Playwright cleanup",
                Status = UpgradeJobStageStatus.Skipped,
                StartedAt = result.StartedAt,
                CompletedAt = result.FinishedAt,
                Message = safeMessage
            }
        ];
    }

    private static bool ShouldRunPlaywrightCleanup(UpgradeJob job)
    {
        return job.Stages.Any(stage =>
            (stage.Name is UpgradeJobStageName.PreUpgradeValidation or UpgradeJobStageName.PostUpgradeValidation) &&
            stage.Status is not UpgradeJobStageStatus.Pending);
    }

    private static UpgradeRollbackEntry? ResolveRollbackEntry(UpgradeJob job, IUpgradeRollbackStore rollbackStore)
    {
        if (!string.IsNullOrWhiteSpace(job.RollbackId) &&
            rollbackStore.TryGet(job.RollbackId, out var entry))
        {
            return entry;
        }

        var latest = rollbackStore.GetLatest();
        return string.Equals(latest?.UpgradeJobId, job.Id, StringComparison.OrdinalIgnoreCase)
            ? latest
            : null;
    }

    private async Task CleanupSupersededRollbackPointsAsync(
        string activeRollbackId,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        var supersededEntries = rollbackStore.MarkSupersededExcept(activeRollbackId);
        foreach (var supersededEntry in supersededEntries)
        {
            await CleanupRollbackPointAsync(supersededEntry, rollbackStore, databaseBackupService, cancellationToken);
        }
    }

    private async Task CleanupFailedNewBackupAsync(
        UpgradeDatabaseBackupResult backupResult,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        if (backupResult.BackupMappings.Count == 0)
        {
            return;
        }

        try
        {
            var cleanupResult = await databaseBackupService.CleanupBackupDatabasesAsync(backupResult, cancellationToken);
            if (!cleanupResult.Succeeded)
            {
                logger.LogWarning(
                    "Partial backup cleanup did not complete after failed backup preparation. Message: {Message}",
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
                "Partial backup cleanup failed unexpectedly after failed backup preparation. SafeMessage: {SafeMessage}",
                Safe(exception.Message));
        }
    }

    private async Task CleanupConsumedRollbackPointAsync(
        string rollbackId,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        if (rollbackStore.TryGet(rollbackId, out var entry))
        {
            await CleanupRollbackPointAsync(entry, rollbackStore, databaseBackupService, cancellationToken);
        }
    }

    private async Task CleanupRollbackPointAsync(
        UpgradeRollbackEntry rollbackEntry,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        try
        {
            var cleanupResult = await databaseBackupService.CleanupBackupDatabasesAsync(rollbackEntry.BackupResult, cancellationToken);
            rollbackStore.MarkCleanupCompleted(rollbackEntry.Id, cleanupResult, cleanupResult.Succeeded);
            if (!cleanupResult.Succeeded)
            {
                logger.LogWarning(
                    "Rollback backup cleanup did not complete. RollbackId: {RollbackId}. Message: {Message}",
                    rollbackEntry.Id,
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
                "Rollback backup cleanup failed unexpectedly. RollbackId: {RollbackId}. SafeMessage: {SafeMessage}",
                rollbackEntry.Id,
                Safe(exception.Message));
            rollbackStore.MarkCleanupCompleted(
                rollbackEntry.Id,
                new BackupCleanupResult(false, $"Backup cleanup failed unexpectedly: {Safe(exception.Message)}"),
                false);
        }
    }

    private static async Task UpdateHistoryCompletionAsync(
        IUpgradeHistoryStore historyStore,
        string jobId,
        UpgradeJobStatus status,
        bool rollbackAvailable,
        string? rollbackJobId,
        UpgradeJobStageName? failureStage,
        string? failureSummary,
        CancellationToken cancellationToken)
    {
        await historyStore.UpdateCompletionAsync(
            jobId,
            status.ToString(),
            DateTimeOffset.UtcNow,
            rollbackAvailable,
            rollbackJobId,
            failureStage?.ToString(),
            string.IsNullOrWhiteSpace(failureSummary) ? null : Safe(failureSummary),
            cancellationToken);
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

    private void FailStageAndJob(string jobId, UpgradeJobStageName stageName, string message)
    {
        var safeMessage = Safe(message);
        jobStore.MarkStageFailed(jobId, stageName, safeMessage);
        jobStore.Complete(jobId, UpgradeJobStatus.Failed, safeMessage);
    }

    private void FailCurrentJob(string jobId, string message)
    {
        var safeMessage = Safe(message);
        if (jobStore.TryGet(jobId, out var job))
        {
            jobStore.MarkStageFailed(jobId, job.CurrentStage, safeMessage);
        }

        jobStore.Complete(jobId, UpgradeJobStatus.Failed, safeMessage);
    }

    private static IReadOnlyList<UpgradeJobOperation> BuildDatabaseBackupOperations(UpgradeDatabaseBackupResult backupResult)
    {
        if (backupResult.BackupSkipped)
        {
            return
            [
                Operation("database-discovery", "Identified affected databases", UpgradeJobStageStatus.Succeeded, BuildDatabaseDiscoveryMessage(backupResult)),
                Operation("database-impact", "Analyzed database impact", UpgradeJobStageStatus.Succeeded, BuildDatabaseImpactMessage(backupResult)),
                Operation("table-discovery", "Identified affected tables", UpgradeJobStageStatus.Skipped, Safe(backupResult.Message)),
                Operation("database-backup", "Backed up affected databases", UpgradeJobStageStatus.Skipped, Safe(backupResult.Message))
            ];
        }

        var operations = new List<UpgradeJobOperation>
        {
            Operation(
                "database-discovery",
                "Identified affected databases",
                backupResult.AnalyzedDatabaseTypes.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                backupResult.AnalyzedDatabaseTypes.Count > 0
                    ? BuildDatabaseDiscoveryMessage(backupResult)
                    : Safe(backupResult.Message)),
            Operation(
                "database-impact",
                "Analyzed database impact",
                backupResult.AnalyzedVersions.Count > 0 && backupResult.AffectedTables.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                BuildDatabaseImpactMessage(backupResult)),
            Operation(
                "table-discovery",
                "Identified affected tables",
                backupResult.AffectedTables.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Skipped,
                backupResult.AffectedTables.Count > 0
                    ? $"Identified {backupResult.AffectedTables.Count} affected table(s)."
                    : "No affected tables were returned for backup.")
        };

        if (backupResult.BackupMappings.Count == 0)
        {
            operations.Add(Operation("database-backup", "Backed up affected databases", UpgradeJobStageStatus.Failed, Safe(backupResult.Message)));
            return operations;
        }

        for (var index = 0; index < backupResult.BackupMappings.Count; index++)
        {
            var mapping = backupResult.BackupMappings[index];
            var tableCount = mapping.BackupStatus?.TablesSucceeded ?? 0;
            var operationMessage = mapping.Succeeded
                ? mapping.BackupStatus is null || string.IsNullOrWhiteSpace(mapping.BackupDatabaseName)
                    ? mapping.UpgradeCreatedTablesToDrop.Count > 0
                        ? $"Recorded {mapping.UpgradeCreatedTablesToDrop.Count} upgrade-created table(s) for rollback cleanup. No physical backup database was required."
                        : "No matching existing affected tables were found. No physical backup database was required."
                    : $"Backed up {tableCount} table(s). Backup identifier: {mapping.BackupDatabaseName}."
                : Safe(mapping.Message);
            operations.Add(Operation(
                $"database-backup-{index + 1}",
                $"{mapping.OriginalDatabaseName} backup",
                mapping.Succeeded ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                operationMessage));
        }

        return operations;
    }

    private static string BuildDatabaseDiscoveryMessage(UpgradeDatabaseBackupResult backupResult)
    {
        var databaseTypeText = backupResult.AnalyzedDatabaseTypes.Count == 0
            ? "not available"
            : string.Join(", ", backupResult.AnalyzedDatabaseTypes);
        var databaseCount = backupResult.BackupMappings.Count;
        return databaseCount == 0
            ? $"Database types analyzed: {databaseTypeText}."
            : $"Database types analyzed: {databaseTypeText}. Databases processed: {databaseCount}.";
    }

    private static string BuildDatabaseImpactMessage(UpgradeDatabaseBackupResult backupResult)
    {
        var versionText = backupResult.AnalyzedVersions.Count == 0
            ? "none"
            : string.Join(", ", backupResult.AnalyzedVersions.Select(FormatVersion));
        var currentVersion = string.IsNullOrWhiteSpace(backupResult.CurrentVersion)
            ? "current version"
            : FormatVersion(backupResult.CurrentVersion);
        var databaseTypes = backupResult.AnalyzedDatabaseTypes.Count == 0
            ? "not available"
            : string.Join(", ", backupResult.AnalyzedDatabaseTypes);

        return $"Current version: {currentVersion}. Target version: {FormatVersion(backupResult.SelectedVersion)}. Versions analyzed: {backupResult.AnalyzedVersions.Count} ({versionText}). Database types analyzed: {databaseTypes}. Unique affected tables: {backupResult.AffectedTables.Count}.";
    }

    private static string FormatVersion(string version)
    {
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }

    private static IReadOnlyList<UpgradeJobOperation> BuildKubernetesUpgradeOperations(KubernetesUpgradeResult kubernetesResult)
    {
        if (kubernetesResult.Skipped)
        {
            return
            [
                Operation("kubernetes-images", "Retrieved target image details", UpgradeJobStageStatus.Skipped, Safe(kubernetesResult.Message)),
                Operation("kubernetes-deployments", "Updated applicable deployment images", UpgradeJobStageStatus.Skipped, Safe(kubernetesResult.Message))
            ];
        }

        var operations = new List<UpgradeJobOperation>
        {
            Operation(
                "kubernetes-images",
                "Retrieved target image details",
                kubernetesResult.DeploymentResults.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                kubernetesResult.DeploymentResults.Count > 0
                    ? $"Retrieved image details for {kubernetesResult.DeploymentResults.Count} deployment(s)."
                    : Safe(kubernetesResult.Message))
        };

        if (kubernetesResult.DeploymentResults.Count == 0)
        {
            operations.Add(Operation("kubernetes-deployments", "Updated applicable deployment images", UpgradeJobStageStatus.Failed, Safe(kubernetesResult.Message)));
            return operations;
        }

        for (var index = 0; index < kubernetesResult.DeploymentResults.Count; index++)
        {
            var deployment = kubernetesResult.DeploymentResults[index];
            operations.Add(Operation(
                KubernetesUpgradeOperationId(deployment.DeploymentName),
                $"{deployment.DeploymentName} image update",
                deployment.Succeeded ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                Safe(deployment.Message)));
        }

        return operations;
    }

    private void UpdateKubernetesProgress(string jobId, KubernetesDeploymentProgress progress)
    {
        var stageName = progress.Kind == KubernetesDeploymentProgressKind.Rollback
            ? UpgradeJobStageName.AutomaticRollback
            : UpgradeJobStageName.KubernetesImageUpgrade;
        var operationId = progress.Kind == KubernetesDeploymentProgressKind.Rollback
            ? KubernetesRollbackOperationId(progress.DeploymentName)
            : KubernetesUpgradeOperationId(progress.DeploymentName);
        var message = Safe(progress.Message);

        switch (progress.Status)
        {
            case KubernetesDeploymentProgressStatus.Completed:
            case KubernetesDeploymentProgressStatus.RollbackCompleted:
                jobStore.MarkOperationSucceeded(jobId, stageName, operationId, message);
                break;
            case KubernetesDeploymentProgressStatus.Failed:
            case KubernetesDeploymentProgressStatus.RollbackFailed:
                jobStore.MarkOperationFailed(jobId, stageName, operationId, message);
                break;
            default:
                jobStore.MarkOperationRunning(jobId, stageName, operationId, message);
                break;
        }
    }

    private void UpdatePlaywrightProgress(string jobId, UpgradeJobStageName stageName, PlaywrightValidationProgress progress)
    {
        var message = Safe(progress.Message);
        switch (progress.Status)
        {
            case UpgradeJobStageStatus.Succeeded:
                jobStore.MarkOperationSucceeded(jobId, stageName, progress.OperationId, message);
                break;
            case UpgradeJobStageStatus.Failed:
                jobStore.MarkOperationFailed(jobId, stageName, progress.OperationId, message);
                break;
            case UpgradeJobStageStatus.Skipped:
                jobStore.MarkOperationSkipped(jobId, stageName, progress.OperationId, message);
                break;
            default:
                jobStore.MarkOperationRunning(jobId, stageName, progress.OperationId, message);
                break;
        }
    }

    private static string KubernetesUpgradeOperationId(string deploymentName)
    {
        return $"kubernetes-deployment-{NormalizeOperationId(deploymentName)}";
    }

    private static string KubernetesRollbackOperationId(string deploymentName)
    {
        return $"kubernetes-rollback-{NormalizeOperationId(deploymentName)}";
    }

    private static string NormalizeOperationId(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static UpgradeJobOperation Operation(string id, string name, UpgradeJobStageStatus status, string? message)
    {
        var now = DateTimeOffset.UtcNow;
        return new UpgradeJobOperation
        {
            Id = id,
            Name = name,
            Status = status,
            StartedAt = now,
            CompletedAt = status is UpgradeJobStageStatus.Pending or UpgradeJobStageStatus.Running ? null : now,
            Message = status == UpgradeJobStageStatus.Failed ? null : Safe(message),
            SafeErrorDetails = status == UpgradeJobStageStatus.Failed ? Safe(message) : null
        };
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No additional details were returned.";
        }

        var sanitized = SensitiveAssignmentPattern.Replace(value, match => $"{match.Groups["key"].Value}=***");
        return sanitized.Length <= 1200 ? sanitized : sanitized[..1200] + "...";
    }
}
