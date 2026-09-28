using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Bold.UpgradeCenter.Services;

// Keeps a hot in-memory cache and writes operational state through to persistent storage.
public sealed class InMemoryUpgradeJobStore : IUpgradeJobStore
{
    private const int RetainedOperationLogJobCount = 7;

    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<key>Password|Pwd|User\s*ID|Username|Token|AccessToken|Secret|Key)\s*=\s*[^;,\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, UpgradeJob> jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> lastLogFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUpgradeOperationalStateStore operationalStateStore;
    private readonly IUpgradeOperationLogStore operationLogStore;
    private readonly object syncRoot = new();

    public InMemoryUpgradeJobStore(
        IUpgradeOperationalStateStore operationalStateStore,
        IUpgradeOperationLogStore operationLogStore)
    {
        this.operationalStateStore = operationalStateStore;
        this.operationLogStore = operationLogStore;
    }

    public UpgradeJob Create(
        string currentVersion,
        string targetVersion,
        UpgradeJobType upgradeType,
        IReadOnlyList<string> selectedDeployments,
        IReadOnlyList<CustomPatchImageMapping>? customImages = null,
        UpgradeUserInfo? initiatedBy = null,
        string? productKey = null)
    {
        var job = new UpgradeJob
        {
            Id = Guid.NewGuid().ToString("N"),
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            UpgradeType = upgradeType,
            ProductKey = UpgradeProductDefinitions.Resolve(productKey).Key,
            SelectedDeployments = selectedDeployments.Count > 0
                ? selectedDeployments.ToArray()
                : [$"All applicable {UpgradeProductDefinitions.Resolve(productKey).DisplayName} deployments"],
            CustomImages = customImages?.ToArray() ?? Array.Empty<CustomPatchImageMapping>(),
            InitiatedBy = initiatedBy ?? UpgradeUserInfo.System(),
            StartedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                CreateStage(
                    UpgradeJobStageName.DatabaseBackup,
                    "Database backup",
                    [
                        CreateOperation("database-discovery", "Identified affected databases"),
                        CreateOperation("database-impact", "Analyzed database impact"),
                        CreateOperation("table-discovery", "Identified affected tables"),
                        CreateOperation("database-backup", "Backed up affected databases")
                    ]),
                CreateStage(
                    UpgradeJobStageName.PreUpgradeValidation,
                    "Pre-upgrade Playwright validation",
                    [
                        CreateOperation("pre-validation-start", "Started Playwright validation"),
                        CreateOperation("pre-validation-tests", "Executed validation tests"),
                        CreateOperation("pre-validation-complete", "Validation completed")
                    ]),
                CreateStage(
                    UpgradeJobStageName.KubernetesImageUpgrade,
                    "Kubernetes image upgrade",
                    [
                        CreateOperation("kubernetes-images", "Retrieved target image details"),
                        CreateOperation("kubernetes-deployments", "Updated applicable deployment images")
                    ]),
                CreateStage(
                    UpgradeJobStageName.PostUpgradeValidation,
                    "Post-upgrade Playwright validation",
                    [
                        CreateOperation("post-validation-wait", "Waiting for upgrade completion"),
                        CreateOperation("post-validation-tests", "Executed validation tests"),
                        CreateOperation("post-validation-complete", "Validation completed")
                    ]),
                CreateStage(
                    UpgradeJobStageName.CleanupJob,
                    "Cleanup Job",
                    [
                        CreateOperation("playwright-prepare", "Prepare Playwright cleanup"),
                        CreateOperation("playwright-shared-state", "Prepare shared Playwright state"),
                        CreateOperation("playwright-secret", "Create temporary runtime Secret"),
                        CreateOperation("playwright-job", "Create Playwright cleanup Job"),
                        CreateOperation("playwright-runner", "Wait for Playwright runner"),
                        CreateOperation("playwright-tests", "Delete Playwright validation resources")
                    ]),
                CreateStage(
                    UpgradeJobStageName.AutomaticRollback,
                    "Automatic rollback",
                    [
                        CreateOperation("rollback-database", "Restored database backup"),
                        CreateOperation("rollback-kubernetes", "Restored previous deployment images")
                    ])
            ]
        };

        jobs[job.Id] = job;
        operationalStateStore.SaveJob(Clone(job));
        AppendJobLog(job, "Info", "Job", $"Upgrade job created. Source version v{currentVersion}; target version v{targetVersion}; operation type {upgradeType}; initiated by {job.InitiatedBy.DisplayName}.");
        return Clone(job);
    }

    public UpgradeJob CreateRollback(
        string currentVersion,
        string targetVersion,
        string rollbackId,
        IReadOnlyList<string> selectedDeployments,
        UpgradeUserInfo? initiatedBy = null,
        string? productKey = null)
    {
        var job = new UpgradeJob
        {
            Id = $"manual-rollback-{Guid.NewGuid():N}",
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            UpgradeType = UpgradeJobType.Rollback,
            ProductKey = UpgradeProductDefinitions.Resolve(productKey).Key,
            SelectedDeployments = selectedDeployments.Count > 0
                ? selectedDeployments.ToArray()
                : ["All recorded rollback targets"],
            CustomImages = Array.Empty<CustomPatchImageMapping>(),
            InitiatedBy = initiatedBy ?? UpgradeUserInfo.System(),
            RollbackId = rollbackId,
            StartedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                CreateStage(
                    UpgradeJobStageName.RollbackDatabaseRestore,
                    "Restore database",
                    [
                        CreateOperation("rollback-database", "Restore affected database tables")
                    ]),
                CreateStage(
                    UpgradeJobStageName.RollbackImageRevert,
                    "Revert Kubernetes image tags",
                    selectedDeployments.Count > 0
                        ? selectedDeployments
                            .Select(deployment => CreateOperation(KubernetesRollbackImageOperationId(deployment), $"{deployment} image reverted"))
                            .ToList()
                        : [CreateOperation("rollback-images", "Revert recorded deployment images")]),
                CreateStage(
                    UpgradeJobStageName.RollbackRolloutWait,
                    "Wait for Kubernetes rollout completion",
                    selectedDeployments.Count > 0
                        ? selectedDeployments
                            .Select(deployment => CreateOperation(KubernetesRollbackRolloutOperationId(deployment), $"{deployment} rollout completed"))
                            .ToList()
                        : [CreateOperation("rollback-rollout", "Wait for deployment rollouts")]),
                CreateStage(
                    UpgradeJobStageName.RollbackHealthVerification,
                    "Verify deployment health",
                    [
                        CreateOperation("rollback-health", "Verify rollout health")
                    ]),
                CreateStage(
                    UpgradeJobStageName.RollbackComplete,
                    "Complete rollback",
                    [
                        CreateOperation("rollback-complete", "Finalize rollback state")
                    ])
            ]
        };

        jobs[job.Id] = job;
        operationalStateStore.SaveJob(Clone(job));
        AppendJobLog(job, "Info", "Job", $"Manual rollback job created. Rollback from v{currentVersion} to v{targetVersion}; initiated by {job.InitiatedBy.DisplayName}.");
        return Clone(job);
    }

    public bool TryGet(string jobId, out UpgradeJob job)
    {
        lock (syncRoot)
        {
            if (jobs.TryGetValue(jobId, out var storedJob))
            {
                job = Clone(storedJob);
                return true;
            }
        }

        if (operationalStateStore.TryGetJob(jobId, out var persistedJob))
        {
            jobs[jobId] = Clone(persistedJob);
            job = Clone(persistedJob);
            return true;
        }

        job = null!;
        return false;
    }

    public bool TryGetLatestActive(out UpgradeJob job)
    {
        lock (syncRoot)
        {
            var activeJob = jobs.Values
                .Where(IsActive)
                .OrderByDescending(item => item.StartedAt)
                .FirstOrDefault();

            if (activeJob is not null)
            {
                job = Clone(activeJob);
                return true;
            }
        }

        if (operationalStateStore.TryGetLatestActiveJob(out var persistedJob))
        {
            jobs[persistedJob.Id] = Clone(persistedJob);
            job = Clone(persistedJob);
            return true;
        }

        job = null!;
        return false;
    }

    public bool TryGetLatest(out UpgradeJob job)
    {
        lock (syncRoot)
        {
            var latestJob = jobs.Values
                .OrderByDescending(item => item.StartedAt)
                .FirstOrDefault();

            if (latestJob is not null)
            {
                job = Clone(latestJob);
                return true;
            }
        }

        if (operationalStateStore.TryGetLatestJob(out var persistedJob))
        {
            jobs[persistedJob.Id] = Clone(persistedJob);
            job = Clone(persistedJob);
            return true;
        }

        job = null!;
        return false;
    }

    public void MarkRunning(string jobId)
    {
        Update(jobId, job =>
        {
            job.Status = UpgradeJobStatus.Running;
            AppendJobLog(job, "Info", "Job", $"Job started. Current status: {job.Status}.");
        });
    }

    public void MarkStageRunning(string jobId, UpgradeJobStageName stageName, string? message = null)
    {
        Update(jobId, job =>
        {
            job.Status = job.CancellationRequested ? UpgradeJobStatus.Cancelling : UpgradeJobStatus.Running;
            job.CurrentStage = stageName;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Running;
            stage.StartedAt ??= DateTimeOffset.UtcNow;
            stage.CompletedAt = null;
            stage.Message = message;
            stage.SafeErrorDetails = null;
            AppendStageLog(job, stage, "Info", "Stage", string.IsNullOrWhiteSpace(message)
                ? $"Stage '{stage.DisplayName}' started."
                : $"Stage '{stage.DisplayName}' started. {message}");
        });
    }

    public void MarkStageSucceeded(string jobId, UpgradeJobStageName stageName, string? message = null)
    {
        Update(jobId, job =>
        {
            var now = DateTimeOffset.UtcNow;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Succeeded;
            stage.CompletedAt = now;
            stage.Message = message;
            stage.SafeErrorDetails = null;
            CompletePendingOperations(stage, UpgradeJobStageStatus.Succeeded, message);
            if (job.UpgradeType == UpgradeJobType.Rollback && stageName == UpgradeJobStageName.RollbackComplete)
            {
                job.Status = UpgradeJobStatus.RollbackSucceeded;
                job.CompletedAt ??= now;
                job.SafeErrorDetails = null;
            }

            AppendStageLog(job, stage, "Info", "Stage", string.IsNullOrWhiteSpace(message)
                ? $"Stage '{stage.DisplayName}' completed successfully."
                : $"Stage '{stage.DisplayName}' completed successfully. {message}");
        });
    }

    public void MarkStageSkipped(string jobId, UpgradeJobStageName stageName, string? message = null)
    {
        Update(jobId, job =>
        {
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Skipped;
            stage.CompletedAt = DateTimeOffset.UtcNow;
            stage.Message = message;
            stage.SafeErrorDetails = null;
            CompletePendingOperations(stage, UpgradeJobStageStatus.Skipped, message);
            AppendStageLog(job, stage, "Info", "Stage", string.IsNullOrWhiteSpace(message)
                ? $"Stage '{stage.DisplayName}' skipped."
                : $"Stage '{stage.DisplayName}' skipped. {message}");
        });
    }

    public void MarkStageFailed(string jobId, UpgradeJobStageName stageName, string safeErrorDetails)
    {
        Update(jobId, job =>
        {
            var now = DateTimeOffset.UtcNow;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Failed;
            stage.CompletedAt = now;
            stage.SafeErrorDetails = safeErrorDetails;
            job.SafeErrorDetails = safeErrorDetails;
            job.CurrentStage = stageName;
            FailRunningOrPendingOperation(stage, safeErrorDetails);
            if (job.UpgradeType == UpgradeJobType.Rollback && stageName == UpgradeJobStageName.RollbackComplete)
            {
                job.Status = UpgradeJobStatus.RollbackFailed;
                job.CompletedAt ??= now;
            }

            AppendStageLog(job, stage, "Error", "Stage", $"Stage '{stage.DisplayName}' failed. {safeErrorDetails}");
        });
    }

    public void MarkStageCancelled(string jobId, UpgradeJobStageName stageName, string message)
    {
        Update(jobId, job =>
        {
            var now = DateTimeOffset.UtcNow;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Cancelled;
            stage.CompletedAt = now;
            stage.Message = message;
            stage.SafeErrorDetails = null;
            job.CurrentStage = stageName;
            CancelRunningOrPendingOperation(stage, message);
            AppendStageLog(job, stage, "Warning", "Stage", $"Stage '{stage.DisplayName}' cancelled. {message}");
        });
    }

    public void ReplaceStageOperations(string jobId, UpgradeJobStageName stageName, IReadOnlyList<UpgradeJobOperation> operations)
    {
        Update(jobId, job =>
        {
            var stage = GetStage(job, stageName);
            stage.Operations.Clear();
            stage.Operations.AddRange(operations.Select(CloneOperation));
            foreach (var operation in stage.Operations.Where(operation => operation.Status != UpgradeJobStageStatus.Pending))
            {
                AppendOperationLog(job, stage, operation, ResolveLogLevel(operation.Status), "OperationResult");
            }
        });
    }

    public void MarkOperationRunning(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null)
    {
        UpdateOperation(jobId, stageName, operationId, (job, stage, operation) =>
        {
            operation.Status = UpgradeJobStageStatus.Running;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = null;
            operation.Message = message;
            operation.SafeErrorDetails = null;
            AppendOperationLog(job, stage, operation, "Info", "Operation");
        });
    }

    public void MarkOperationSucceeded(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null)
    {
        UpdateOperation(jobId, stageName, operationId, (job, stage, operation) =>
        {
            operation.Status = UpgradeJobStageStatus.Succeeded;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message = message;
            operation.SafeErrorDetails = null;
            AppendOperationLog(job, stage, operation, "Info", "Operation");
        });
    }

    public void MarkOperationSkipped(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null)
    {
        UpdateOperation(jobId, stageName, operationId, (job, stage, operation) =>
        {
            operation.Status = UpgradeJobStageStatus.Skipped;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message = message;
            operation.SafeErrorDetails = null;
            AppendOperationLog(job, stage, operation, "Info", "Operation");
        });
    }

    public void MarkOperationFailed(string jobId, UpgradeJobStageName stageName, string operationId, string safeErrorDetails)
    {
        UpdateOperation(jobId, stageName, operationId, (job, stage, operation) =>
        {
            operation.Status = UpgradeJobStageStatus.Failed;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.SafeErrorDetails = safeErrorDetails;
            AppendOperationLog(job, stage, operation, "Error", "Operation");
        });
    }

    public void MarkRollbackAvailable(string jobId, string rollbackId)
    {
        Update(jobId, job =>
        {
            job.RollbackAvailable = true;
            job.RollbackId = rollbackId;
            AppendJobLog(job, "Info", "Rollback", $"Rollback point recorded. rollbackId={rollbackId}.");
        });
    }

    public bool RequestCancellation(string jobId, string requestedBy, string message)
    {
        var requested = false;
        Update(jobId, job =>
        {
            if (!IsActive(job) || job.UpgradeType == UpgradeJobType.Rollback)
            {
                return;
            }

            job.CancellationRequested = true;
            job.CancellationRequestedAt ??= DateTimeOffset.UtcNow;
            job.CancellationRequestedBy = requestedBy;
            job.CancellationMessage = message;
            job.Status = UpgradeJobStatus.Cancelling;
            AppendJobLog(job, "Warning", "Cancellation", $"{message} Requested by {requestedBy}.");
            requested = true;
        });

        return requested;
    }

    public void MarkCancelling(string jobId, string message)
    {
        Update(jobId, job =>
        {
            job.CancellationRequested = true;
            job.CancellationRequestedAt ??= DateTimeOffset.UtcNow;
            job.CancellationMessage = message;
            job.Status = UpgradeJobStatus.Cancelling;
            AppendJobLog(job, "Warning", "Cancellation", message);
        });
    }

    public void Complete(string jobId, UpgradeJobStatus status, string? safeErrorDetails = null)
    {
        Update(jobId, job =>
        {
            job.Status = status;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.SafeErrorDetails = safeErrorDetails;
            var completionMessage = status is UpgradeJobStatus.Failed or UpgradeJobStatus.RollbackFailed
                ? $"Job completed. Final status: {status}. Review the failed stage log above for the detailed reason."
                : string.IsNullOrWhiteSpace(safeErrorDetails)
                    ? $"Job completed. Final status: {status}."
                    : $"Job completed. Final status: {status}. {safeErrorDetails}";
            AppendJobLog(
                job,
                status is UpgradeJobStatus.Failed or UpgradeJobStatus.RollbackFailed ? "Error" : "Info",
                "Job",
                completionMessage);
            operationLogStore.RetainLatestJobs(RetainedOperationLogJobCount);
        });
    }

    private void Update(string jobId, Action<UpgradeJob> update)
    {
        lock (syncRoot)
        {
            if (jobs.TryGetValue(jobId, out var job))
            {
                update(job);
                operationalStateStore.SaveJob(Clone(job));
            }
        }
    }

    private static bool IsActive(UpgradeJob job)
    {
        return job.Status is UpgradeJobStatus.Queued or UpgradeJobStatus.Running or UpgradeJobStatus.Cancelling;
    }

    private static UpgradeJobStage GetStage(UpgradeJob job, UpgradeJobStageName stageName)
    {
        return job.Stages.First(stage => stage.Name == stageName);
    }

    private void UpdateOperation(string jobId, UpgradeJobStageName stageName, string operationId, Action<UpgradeJob, UpgradeJobStage, UpgradeJobOperation> update)
    {
        Update(jobId, job =>
        {
            var stage = GetStage(job, stageName);
            var operation = stage.Operations.FirstOrDefault(item => string.Equals(item.Id, operationId, StringComparison.OrdinalIgnoreCase));
            if (operation is null)
            {
                operation = CreateOperation(operationId, operationId);
                stage.Operations.Add(operation);
            }

            update(job, stage, operation);
        });
    }

    private static UpgradeJobStage CreateStage(UpgradeJobStageName name, string displayName, IReadOnlyList<UpgradeJobOperation> operations)
    {
        return new UpgradeJobStage
        {
            Name = name,
            DisplayName = displayName,
            Operations = operations.Select(CloneOperation).ToList()
        };
    }

    private static UpgradeJobOperation CreateOperation(string id, string name)
    {
        return new UpgradeJobOperation
        {
            Id = id,
            Name = string.Equals(id, name, StringComparison.OrdinalIgnoreCase) ? CreateDisplayName(id) : name
        };
    }

    private static string CreateDisplayName(string id)
    {
        var words = id.Replace("-", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(words)
            ? id
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public static string KubernetesRollbackImageOperationId(string deploymentName)
    {
        return $"rollback-image-{NormalizeOperationId(deploymentName)}";
    }

    public static string KubernetesRollbackRolloutOperationId(string deploymentName)
    {
        return $"rollback-rollout-{NormalizeOperationId(deploymentName)}";
    }

    private static string NormalizeOperationId(string value)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static void CompletePendingOperations(UpgradeJobStage stage, UpgradeJobStageStatus status, string? message)
    {
        foreach (var operation in stage.Operations.Where(operation => operation.Status is UpgradeJobStageStatus.Pending or UpgradeJobStageStatus.Running))
        {
            operation.Status = status;
            operation.StartedAt ??= stage.StartedAt ?? DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message ??= message;
        }
    }

    private static void FailRunningOrPendingOperation(UpgradeJobStage stage, string safeErrorDetails)
    {
        var operation = stage.Operations.FirstOrDefault(item => item.Status == UpgradeJobStageStatus.Running)
            ?? stage.Operations.FirstOrDefault(item => item.Status == UpgradeJobStageStatus.Pending);
        if (operation is null)
        {
            return;
        }

        operation.Status = UpgradeJobStageStatus.Failed;
        operation.StartedAt ??= stage.StartedAt ?? DateTimeOffset.UtcNow;
        operation.CompletedAt = DateTimeOffset.UtcNow;
        operation.SafeErrorDetails = safeErrorDetails;
    }

    private static void CancelRunningOrPendingOperation(UpgradeJobStage stage, string message)
    {
        foreach (var operation in stage.Operations.Where(item => item.Status is UpgradeJobStageStatus.Running or UpgradeJobStageStatus.Pending))
        {
            operation.Status = UpgradeJobStageStatus.Cancelled;
            operation.StartedAt ??= stage.StartedAt ?? DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message ??= message;
        }
    }

    private void AppendJobLog(UpgradeJob job, string level, string source, string message)
    {
        AppendLog(job.Id, null, level, source, message);
    }

    private void AppendStageLog(UpgradeJob job, UpgradeJobStage stage, string level, string source, string message)
    {
        if (ShouldSuppressStageLog(stage, source))
        {
            return;
        }

        AppendLog(job.Id, stage.DisplayName, level, source, message);
    }

    private void AppendOperationLog(UpgradeJob job, UpgradeJobStage stage, UpgradeJobOperation operation, string level, string source)
    {
        if (string.Equals(source, "OperationResult", StringComparison.Ordinal) ||
            ShouldSuppressOperationLog(stage, operation, source))
        {
            return;
        }

        var detail = operation.SafeErrorDetails ?? operation.Message;
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"{operation.Name}: {operation.Status}."
            : $"{operation.Name}: {operation.Status}. {detail}";

        if (operation.StartedAt.HasValue && operation.CompletedAt.HasValue &&
            !ContainsDuration(message))
        {
            var duration = operation.CompletedAt.Value - operation.StartedAt.Value;
            message += $" Duration {FormatDuration(duration)}.";
        }

        AppendLog(job.Id, stage.DisplayName, level, source, message, operation.Id, operation.Status);
    }

    private void AppendLog(
        string jobId,
        string? stage,
        string level,
        string source,
        string message,
        string? operationId = null,
        UpgradeJobStageStatus? status = null)
    {
        var safeMessage = RedactSensitive(message);
        var fingerprintKey = string.Join(
            "|",
            jobId,
            stage ?? string.Empty,
            source,
            operationId ?? string.Empty);
        var fingerprintValue = string.Join("|", level, status?.ToString() ?? string.Empty, safeMessage);

        if (lastLogFingerprints.TryGetValue(fingerprintKey, out var previous) &&
            string.Equals(previous, fingerprintValue, StringComparison.Ordinal))
        {
            return;
        }

        lastLogFingerprints[fingerprintKey] = fingerprintValue;
        operationLogStore.Append(jobId, stage, level, source, safeMessage);
    }

    private static bool ShouldSuppressStageLog(UpgradeJobStage stage, string source)
    {
        if (!string.Equals(source, "Stage", StringComparison.Ordinal))
        {
            return false;
        }

        if (stage.Status is not (UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Failed))
        {
            return false;
        }

        return stage.Name is UpgradeJobStageName.PreUpgradeValidation
            or UpgradeJobStageName.PostUpgradeValidation
            or UpgradeJobStageName.CleanupJob;
    }

    private static bool ShouldSuppressOperationLog(UpgradeJobStage stage, UpgradeJobOperation operation, string source)
    {
        if (!string.Equals(source, "Operation", StringComparison.Ordinal))
        {
            return false;
        }

        if (stage.Name == UpgradeJobStageName.PreUpgradeValidation)
        {
            return operation.Status != UpgradeJobStageStatus.Failed &&
                   (operation.Id is "pre-validation-start" or "pre-validation-tests" or "pre-validation-complete");
        }

        if (stage.Name == UpgradeJobStageName.PostUpgradeValidation)
        {
            return operation.Status != UpgradeJobStageStatus.Failed &&
                   (operation.Id is "post-validation-tests" or "post-validation-complete");
        }

        return false;
    }

    private static bool ContainsDuration(string message)
    {
        return message.Contains("Duration:", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Duration ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLogLevel(UpgradeJobStageStatus status)
    {
        return status switch
        {
            UpgradeJobStageStatus.Failed => "Error",
            UpgradeJobStageStatus.Cancelled => "Warning",
            _ => "Info"
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0}s"
            : $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    private static string RedactSensitive(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return SensitiveAssignmentPattern.Replace(value, "${key}=***");
    }

    private static UpgradeJob Clone(UpgradeJob job)
    {
        return new UpgradeJob
        {
            Id = job.Id,
            CurrentVersion = job.CurrentVersion,
            TargetVersion = job.TargetVersion,
            UpgradeType = job.UpgradeType,
            ProductKey = job.ProductKey,
            SelectedDeployments = job.SelectedDeployments.ToArray(),
            CustomImages = job.CustomImages.ToArray(),
            InitiatedBy = job.InitiatedBy,
            CurrentStage = job.CurrentStage,
            Status = job.Status,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            SafeErrorDetails = job.SafeErrorDetails,
            RollbackAvailable = job.RollbackAvailable,
            RollbackId = job.RollbackId,
            Stages = job.Stages.Select(stage => new UpgradeJobStage
            {
                Name = stage.Name,
                DisplayName = stage.DisplayName,
                Status = stage.Status,
                StartedAt = stage.StartedAt,
                CompletedAt = stage.CompletedAt,
                Message = stage.Message,
                SafeErrorDetails = stage.SafeErrorDetails,
                Operations = stage.Operations.Select(CloneOperation).ToList()
            }).ToList()
        };
    }

    private static UpgradeJobOperation CloneOperation(UpgradeJobOperation operation)
    {
        return new UpgradeJobOperation
        {
            Id = operation.Id,
            Name = operation.Name,
            Status = operation.Status,
            StartedAt = operation.StartedAt,
            CompletedAt = operation.CompletedAt,
            Message = operation.Message,
            SafeErrorDetails = operation.SafeErrorDetails
        };
    }
}
