using UiJob = Bold.UpgradeCenter.Models.UpgradeJob;
using UiJobStage = Bold.UpgradeCenter.Models.JobStage;
using UiReleaseType = Bold.UpgradeCenter.Models.ReleaseType;
using UiStageDetailStatus = Bold.UpgradeCenter.Models.StageDetailStatus;
using UiStageCheckResult = Bold.UpgradeCenter.Models.StageCheckResult;
using UiStageStatus = Bold.UpgradeCenter.Models.StageStatus;
using UiUpgradeJobStatus = Bold.UpgradeCenter.Models.UpgradeJobStatus;

namespace Bold.UpgradeCenter.Services;

internal static class UpgradeJobViewModelMapper
{
    private const string CleanupManualActionMessage = "Cleanup could not be completed. Manual cleanup is required.";

    public static UiJob ToViewModel(UpgradeJob job)
    {
        return new UiJob
        {
            JobId = job.Id,
            FromVersion = job.CurrentVersion,
            ToVersion = job.TargetVersion,
            Status = ToViewModelStatus(job.Status, job.UpgradeType),
            StartedAt = job.StartedAt.UtcDateTime,
            CompletedAt = job.CompletedAt?.UtcDateTime,
            InitiatedBy = job.InitiatedBy.DisplayName,
            ReleaseType = job.UpgradeType == UpgradeJobType.CustomVersionOrPatch ? UiReleaseType.CustomPatch : UiReleaseType.Standard,
            ProductName = UpgradeProductDefinitions.Resolve(job.ProductKey).DisplayName,
            RevertsJobId = job.UpgradeType == UpgradeJobType.Rollback ? job.RollbackId ?? job.Id : null,
            RollbackJobId = job.RollbackAvailable ? null : job.RollbackId,
            CancellationRequested = job.CancellationRequested,
            CancellationMessage = job.CancellationMessage,
            CompleteLogLines = [],
            CompleteLogEntries = [],
            Stages = job.Stages
                .Where(stage => ShouldRenderStage(stage, job.UpgradeType))
                .Select(stage => NormalizeStageForTerminalJob(stage, job.Status, job.CompletedAt))
                .Select(ToViewModelStage)
                .ToList()
        };
    }

    public static UiJob ToViewModel(UpgradeHistoryRecord record)
    {
        return new UiJob
        {
            JobId = record.JobId,
            FromVersion = record.PreviousVersion ?? string.Empty,
            ToVersion = record.TargetVersion ?? string.Empty,
            Status = ToViewModelStatus(record.Status, record.OperationType),
            StartedAt = record.StartedAt.UtcDateTime,
            CompletedAt = record.CompletedAt?.UtcDateTime,
            InitiatedBy = string.IsNullOrWhiteSpace(record.InitiatedByName) ? record.InitiatedByUserId : record.InitiatedByName,
            ProductName = "Product",
            ReleaseType = string.Equals(record.OperationType, UpgradeHistoryOperationTypes.CustomVersionOrPatchUpgrade, StringComparison.OrdinalIgnoreCase)
                ? UiReleaseType.CustomPatch
                : UiReleaseType.Standard,
            RevertsJobId = record.ParentJobId,
            CompleteLogLines = [],
            CompleteLogEntries = []
        };
    }

    public static UiUpgradeJobStatus ToViewModelStatus(string status, string? operationType)
    {
        return status switch
        {
            "Succeeded" => UiUpgradeJobStatus.Completed,
            "Failed" => UiUpgradeJobStatus.Failed,
            "Cancelled" => UiUpgradeJobStatus.Cancelled,
            "Cancelling" => UiUpgradeJobStatus.Cancelling,
            "RollbackSucceeded" => UiUpgradeJobStatus.RolledBack,
            "RollbackFailed" => UiUpgradeJobStatus.Failed,
            "Running" when string.Equals(operationType, UpgradeHistoryOperationTypes.ManualRollback, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(operationType, UpgradeHistoryOperationTypes.AutomaticRollback, StringComparison.OrdinalIgnoreCase) => UiUpgradeJobStatus.RollingBack,
            "Running" => UiUpgradeJobStatus.Running,
            _ => UiUpgradeJobStatus.Pending
        };
    }

    private static UiUpgradeJobStatus ToViewModelStatus(UpgradeJobStatus status, UpgradeJobType upgradeType)
    {
        return status switch
        {
            UpgradeJobStatus.Succeeded => UiUpgradeJobStatus.Completed,
            UpgradeJobStatus.Failed => UiUpgradeJobStatus.Failed,
            UpgradeJobStatus.Cancelled => UiUpgradeJobStatus.Cancelled,
            UpgradeJobStatus.Cancelling => UiUpgradeJobStatus.Cancelling,
            UpgradeJobStatus.RollbackSucceeded => UiUpgradeJobStatus.RolledBack,
            UpgradeJobStatus.RollbackFailed => UiUpgradeJobStatus.Failed,
            UpgradeJobStatus.Running when upgradeType == UpgradeJobType.Rollback => UiUpgradeJobStatus.RollingBack,
            UpgradeJobStatus.Running => UiUpgradeJobStatus.Running,
            _ => UiUpgradeJobStatus.Pending
        };
    }

    private static bool ShouldRenderStage(UpgradeJobStage stage, UpgradeJobType upgradeType)
    {
        if (upgradeType == UpgradeJobType.Rollback)
        {
            return stage.Name is UpgradeJobStageName.RollbackDatabaseRestore
                or UpgradeJobStageName.RollbackImageRevert
                or UpgradeJobStageName.RollbackRolloutWait
                or UpgradeJobStageName.RollbackHealthVerification
                or UpgradeJobStageName.RollbackComplete;
        }

        return stage.Name is UpgradeJobStageName.PreUpgradeValidation
            or UpgradeJobStageName.DatabaseBackup
            or UpgradeJobStageName.KubernetesImageUpgrade
            or UpgradeJobStageName.PostUpgradeValidation
            or UpgradeJobStageName.CleanupJob;
    }

    private static UiJobStage ToViewModelStage(UpgradeJobStage stage)
    {
        return new UiJobStage
        {
            Name = ToViewModelStageName(stage),
            Meta = BuildStageMeta(stage),
            Status = ToViewModelStageStatus(stage.Status),
            ProgressPercent = CalculateProgressPercent(stage),
            Duration = stage.StartedAt.HasValue && stage.CompletedAt.HasValue
                ? stage.CompletedAt.Value - stage.StartedAt.Value
                : null,
            Checks = BuildStageChecks(stage),
            LogLines = stage.Operations
                .Where(operation => !string.IsNullOrWhiteSpace(operation.Message) || !string.IsNullOrWhiteSpace(operation.SafeErrorDetails))
                .Select(operation => $"{operation.Name}: {operation.SafeErrorDetails ?? operation.Message}")
                .ToList()
        };
    }

    private static UpgradeJobStage NormalizeStageForTerminalJob(
        UpgradeJobStage stage,
        UpgradeJobStatus jobStatus,
        DateTimeOffset? jobCompletedAt)
    {
        if (jobStatus is UpgradeJobStatus.Queued or UpgradeJobStatus.Running or UpgradeJobStatus.Cancelling ||
            stage.Status is not (UpgradeJobStageStatus.Running or UpgradeJobStageStatus.Pending))
        {
            return stage;
        }

        var stageStarted = HasStageStarted(stage);
        if (!stageStarted)
        {
            return new UpgradeJobStage
            {
                Name = stage.Name,
                DisplayName = stage.DisplayName,
                Status = UpgradeJobStageStatus.Skipped,
                StartedAt = stage.StartedAt,
                CompletedAt = jobCompletedAt ?? DateTimeOffset.UtcNow,
                Message = stage.Name == UpgradeJobStageName.CleanupJob
                    ? "Cleanup was not required because Playwright validation did not create resources."
                    : "Stage was skipped because the operation stopped before this stage started.",
                Operations = stage.Operations.Select(operation => NormalizeOperationForTerminalStage(operation, UpgradeJobStageStatus.Skipped, jobCompletedAt ?? DateTimeOffset.UtcNow)).ToList()
            };
        }

        var normalizedStatus = jobStatus switch
        {
            UpgradeJobStatus.Failed or UpgradeJobStatus.RollbackFailed => UpgradeJobStageStatus.Failed,
            UpgradeJobStatus.Cancelled or UpgradeJobStatus.RollbackSucceeded => UpgradeJobStageStatus.Cancelled,
            _ => UpgradeJobStageStatus.Skipped
        };
        var message = normalizedStatus switch
        {
            UpgradeJobStageStatus.Failed => stage.SafeErrorDetails ?? stage.Message ?? "Stage did not complete before the job reached a failed state.",
            UpgradeJobStageStatus.Cancelled => stage.Message ?? "Stage was stopped because rollback completed.",
            _ => stage.Message ?? "Stage was not required after the job reached a terminal state."
        };
        var completedAt = jobCompletedAt ?? stage.CompletedAt ?? DateTimeOffset.UtcNow;

        return new UpgradeJobStage
        {
            Name = stage.Name,
            DisplayName = stage.DisplayName,
            Status = normalizedStatus,
            StartedAt = stage.StartedAt,
            CompletedAt = completedAt,
            Message = normalizedStatus == UpgradeJobStageStatus.Failed ? null : message,
            SafeErrorDetails = normalizedStatus == UpgradeJobStageStatus.Failed ? message : null,
            Operations = stage.Operations.Select(operation => NormalizeOperationForTerminalStage(operation, normalizedStatus, completedAt)).ToList()
        };
    }

    private static UpgradeJobOperation NormalizeOperationForTerminalStage(
        UpgradeJobOperation operation,
        UpgradeJobStageStatus stageStatus,
        DateTimeOffset completedAt)
    {
        if (operation.Status is not (UpgradeJobStageStatus.Running or UpgradeJobStageStatus.Pending))
        {
            return operation;
        }

        var operationStatus = operation.Status == UpgradeJobStageStatus.Pending && !HasOperationStarted(operation)
            ? UpgradeJobStageStatus.Skipped
            : stageStatus;
        var message = operationStatus switch
        {
            UpgradeJobStageStatus.Failed => operation.SafeErrorDetails ?? operation.Message ?? "Operation did not complete before the job failed.",
            UpgradeJobStageStatus.Cancelled => operation.Message ?? "Operation was stopped because rollback completed.",
            _ => operation.Message ?? "Operation was not required after the job reached a terminal state."
        };

        return new UpgradeJobOperation
        {
            Id = operation.Id,
            Name = operation.Name,
            Status = operationStatus,
            StartedAt = operation.StartedAt,
            CompletedAt = completedAt,
            Message = operationStatus == UpgradeJobStageStatus.Failed ? null : message,
            SafeErrorDetails = operationStatus == UpgradeJobStageStatus.Failed ? message : null
        };
    }

    private static string? BuildStageMeta(UpgradeJobStage stage)
    {
        if (stage.Name == UpgradeJobStageName.DatabaseBackup)
        {
            if (!string.IsNullOrWhiteSpace(stage.SafeErrorDetails))
            {
                return stage.SafeErrorDetails;
            }

            return stage.Status switch
            {
                UpgradeJobStageStatus.Succeeded => "Schema backup completed.",
                UpgradeJobStageStatus.Running => "Schema backup is in progress.",
                UpgradeJobStageStatus.Failed => "Schema backup failed.",
                UpgradeJobStageStatus.Skipped => "Schema backup was skipped.",
                _ => stage.Message
            };
        }

        return stage.Name switch
        {
            UpgradeJobStageName.PreUpgradeValidation => BuildValidationStageMeta(stage, "Pre-upgrade validation"),
            UpgradeJobStageName.PostUpgradeValidation => BuildValidationStageMeta(stage, "Post-upgrade validation"),
            UpgradeJobStageName.CleanupJob => BuildLifecycleStageMeta(stage, "Cleanup Job"),
            UpgradeJobStageName.KubernetesImageUpgrade => BuildLifecycleStageMeta(stage, "Kubernetes upgrade"),
            UpgradeJobStageName.RollbackDatabaseRestore => BuildLifecycleStageMeta(stage, "Database restore"),
            UpgradeJobStageName.RollbackImageRevert => BuildLifecycleStageMeta(stage, "Kubernetes image revert"),
            UpgradeJobStageName.RollbackRolloutWait => BuildLifecycleStageMeta(stage, "Kubernetes rollout wait"),
            UpgradeJobStageName.RollbackHealthVerification => BuildLifecycleStageMeta(stage, "Deployment health verification"),
            UpgradeJobStageName.RollbackComplete => BuildLifecycleStageMeta(stage, "Rollback finalization"),
            UpgradeJobStageName.AutomaticRollback => BuildLifecycleStageMeta(stage, "Automatic rollback"),
            _ => stage.Message
        };
    }

    private static int CalculateProgressPercent(UpgradeJobStage stage)
    {
        if (stage.Status == UpgradeJobStageStatus.Skipped)
        {
            return 0;
        }

        if (stage.Name is UpgradeJobStageName.PreUpgradeValidation or UpgradeJobStageName.PostUpgradeValidation)
        {
            return CalculatePlaywrightValidationProgressPercent(stage);
        }

        if (stage.Name == UpgradeJobStageName.KubernetesImageUpgrade)
        {
            return CalculateDeploymentProgressPercent(stage, "kubernetes-deployment-");
        }

        if (stage.Name == UpgradeJobStageName.CleanupJob)
        {
            return CalculateCleanupProgressPercent(stage);
        }

        if (stage.Name == UpgradeJobStageName.RollbackImageRevert)
        {
            return CalculateDeploymentProgressPercent(stage, "rollback-image-");
        }

        if (stage.Name == UpgradeJobStageName.RollbackRolloutWait)
        {
            return CalculateDeploymentProgressPercent(stage, "rollback-rollout-");
        }

        if (stage.Operations.Count == 0)
        {
            return stage.Status switch
            {
                UpgradeJobStageStatus.Succeeded => 100,
                _ => 0
            };
        }

        var completed = stage.Operations.Count(operation =>
            HasOperationStarted(operation) &&
            operation.Status is UpgradeJobStageStatus.Succeeded
                or UpgradeJobStageStatus.Skipped
                or UpgradeJobStageStatus.Failed
                or UpgradeJobStageStatus.Cancelled);
        return Math.Clamp((int)Math.Round((double)completed / stage.Operations.Count * 100), 0, 100);
    }

    private static List<UiStageCheckResult> BuildStageChecks(UpgradeJobStage stage)
    {
        return stage.Name switch
        {
            UpgradeJobStageName.DatabaseBackup => BuildSchemaBackupChecks(stage),
            UpgradeJobStageName.PreUpgradeValidation => BuildPlaywrightValidationChecks(stage, isPostUpgrade: false),
            UpgradeJobStageName.PostUpgradeValidation => BuildPlaywrightValidationChecks(stage, isPostUpgrade: true),
            UpgradeJobStageName.CleanupJob => BuildCleanupJobChecks(stage),
            UpgradeJobStageName.KubernetesImageUpgrade => BuildKubernetesUpgradeChecks(stage),
            UpgradeJobStageName.RollbackDatabaseRestore
                or UpgradeJobStageName.RollbackImageRevert
                or UpgradeJobStageName.RollbackRolloutWait
                or UpgradeJobStageName.RollbackHealthVerification
                or UpgradeJobStageName.RollbackComplete
                or UpgradeJobStageName.AutomaticRollback => BuildRollbackChecks(stage),
            _ => BuildGenericChecks(stage)
        };
    }

    private static string? BuildValidationStageMeta(UpgradeJobStage stage, string label)
    {
        if (!string.IsNullOrWhiteSpace(stage.SafeErrorDetails))
        {
            return BuildPlaywrightStageSummary(stage, isCleanup: false);
        }

        return stage.Status switch
        {
            UpgradeJobStageStatus.Succeeded => $"{label} completed.",
            UpgradeJobStageStatus.Running => $"{label} is in progress.",
            UpgradeJobStageStatus.Failed => $"{label} failed.",
            UpgradeJobStageStatus.Skipped => $"{label} was skipped.",
            _ => stage.Message
        };
    }

    private static string? BuildLifecycleStageMeta(UpgradeJobStage stage, string label)
    {
        if (stage.Name == UpgradeJobStageName.CleanupJob)
        {
            return BuildCleanupStageSummary(stage);
        }

        if (!string.IsNullOrWhiteSpace(stage.SafeErrorDetails))
        {
            return stage.SafeErrorDetails;
        }

        return stage.Status switch
        {
            UpgradeJobStageStatus.Succeeded => $"{label} completed.",
            UpgradeJobStageStatus.Running => $"{label} is in progress.",
            UpgradeJobStageStatus.Failed => $"{label} failed.",
            UpgradeJobStageStatus.Skipped => $"{label} was skipped.",
            _ => stage.Message
        };
    }

    private static string ToViewModelStageName(UpgradeJobStage stage)
    {
        return stage.Name switch
        {
            UpgradeJobStageName.PreUpgradeValidation => "Pre-Upgrade Validation",
            UpgradeJobStageName.DatabaseBackup => "Schema Backup",
            UpgradeJobStageName.KubernetesImageUpgrade => "Upgrade",
            UpgradeJobStageName.PostUpgradeValidation => "Post-Upgrade Validation",
            UpgradeJobStageName.CleanupJob => "Cleanup Job",
            _ => stage.DisplayName
        };
    }

    private static UiStageStatus ToViewModelStageStatus(UpgradeJobStageStatus status)
    {
        return status switch
        {
            UpgradeJobStageStatus.Running => UiStageStatus.Running,
            UpgradeJobStageStatus.Succeeded => UiStageStatus.Passed,
            UpgradeJobStageStatus.Failed => UiStageStatus.Failed,
            UpgradeJobStageStatus.Cancelled => UiStageStatus.Cancelled,
            UpgradeJobStageStatus.Skipped => UiStageStatus.Skipped,
            _ => UiStageStatus.Pending
        };
    }

    private static UiStageDetailStatus ToViewModelDetailStatus(UpgradeJobStageStatus status)
    {
        return status switch
        {
            UpgradeJobStageStatus.Running => UiStageDetailStatus.Running,
            UpgradeJobStageStatus.Succeeded => UiStageDetailStatus.Completed,
            UpgradeJobStageStatus.Failed => UiStageDetailStatus.Failed,
            UpgradeJobStageStatus.Cancelled => UiStageDetailStatus.Warning,
            UpgradeJobStageStatus.Skipped => UiStageDetailStatus.Skipped,
            _ => UiStageDetailStatus.Pending
        };
    }

    private static List<UiStageCheckResult> BuildPlaywrightValidationChecks(UpgradeJobStage stage, bool isPostUpgrade)
    {
        var checks = new List<UiStageCheckResult>();

        if (isPostUpgrade && FindOperation(stage, "post-validation-wait") is { } waitOperation)
        {
            checks.Add(new UiStageCheckResult
            {
                Description = BuildStepDescription(
                    waitOperation.Status,
                    "Kubernetes image upgrade completed; starting post-upgrade validation",
                    "Waiting for Kubernetes image upgrade to complete",
                    "Wait for Kubernetes image upgrade completion",
                    "Kubernetes image upgrade did not complete before post-upgrade validation"),
                Status = ToViewModelDetailStatus(waitOperation.Status)
            });
        }

        AddOptionalCheck(
            checks,
            stage,
            "playwright-secret",
            completed: "Temporary runtime Secret created",
            running: "Creating temporary runtime Secret",
            pending: "Create temporary runtime Secret",
            failed: "Could not create temporary runtime Secret");
        AddOptionalCheck(
            checks,
            stage,
            "playwright-shared-state",
            completed: "Shared Playwright validation state volume is ready",
            running: "Preparing shared Playwright validation state volume",
            pending: "Prepare shared Playwright validation state volume",
            failed: "Could not prepare shared Playwright validation state volume");
        AddOptionalCheck(
            checks,
            stage,
            "playwright-job",
            completed: "Playwright validation Job created",
            running: "Creating Playwright validation Job",
            pending: "Create Playwright validation Job",
            failed: "Could not create Playwright validation Job");

        var testsOperation = FindOperation(stage, "product-health-validation")
            ?? FindOperation(stage, "playwright-tests")
            ?? FindOperation(stage, isPostUpgrade ? "post-validation-tests" : "pre-validation-tests");
        if (testsOperation is not null)
        {
            checks.Add(new UiStageCheckResult
            {
                Description = BuildPlaywrightTestsDescription(testsOperation, isCleanup: false),
                Status = ToViewModelDetailStatus(testsOperation.Status)
            });
        }

        return checks.Count > 0 ? checks : BuildGenericChecks(stage);
    }

    private static List<UiStageCheckResult> BuildKubernetesUpgradeChecks(UpgradeJobStage stage)
    {
        var checks = new List<UiStageCheckResult>();
        var imageOperation = FindOperation(stage, "kubernetes-images");
        if (imageOperation is not null)
        {
            var deploymentCount = ExtractNumber(imageOperation.Message, @"for\s+(\d+)\s+deployment");
            checks.Add(new UiStageCheckResult
            {
                Description = BuildStepDescription(
                    imageOperation.Status,
                    deploymentCount.HasValue
                        ? $"Retrieved target image details for {deploymentCount.Value} {Pluralize(deploymentCount.Value, "deployment", "deployments")}"
                        : "Retrieved target image details",
                    "Retrieving target image details",
                    "Retrieve target image details",
                    "Could not retrieve target image details"),
                Status = ToViewModelDetailStatus(imageOperation.Status)
            });
        }

        foreach (var operation in stage.Operations.Where(operation => operation.Id.StartsWith("kubernetes-deployment-", StringComparison.OrdinalIgnoreCase)))
        {
            var deploymentName = ResolveDeploymentName(operation, " image update");
            checks.Add(new UiStageCheckResult
            {
                Description = BuildStepDescription(
                    operation.Status,
                    $"{deploymentName} image updated and rollout is healthy",
                    $"{deploymentName} image update and rollout are in progress",
                    $"{deploymentName} image update is pending",
                    $"{deploymentName} image update or rollout failed"),
                Status = ToViewModelDetailStatus(operation.Status)
            });
        }

        return checks.Count > 0 ? checks : BuildGenericChecks(stage);
    }

    private static List<UiStageCheckResult> BuildCleanupJobChecks(UpgradeJobStage stage)
    {
        var skippedCleanup = FindOperation(stage, "playwright-cleanup-skipped");
        if (stage.Status == UpgradeJobStageStatus.Skipped || skippedCleanup is not null)
        {
            return
            [
                new UiStageCheckResult
                {
                    Description = skippedCleanup?.Message
                        ?? stage.Message
                        ?? "Cleanup Job skipped because the Kubernetes Playwright runner is disabled.",
                    Status = ToViewModelDetailStatus(UpgradeJobStageStatus.Skipped)
                }
            ];
        }

        var checks = new List<UiStageCheckResult>();
        AddOptionalCheck(
            checks,
            stage,
            "playwright-shared-state",
            completed: "Shared Playwright state volume is ready",
            running: "Preparing shared Playwright state volume",
            pending: "Prepare shared Playwright state volume",
            failed: "Could not prepare shared Playwright state volume");
        AddOptionalCheck(
            checks,
            stage,
            "playwright-secret",
            completed: "Temporary runtime Secret created",
            running: "Creating temporary runtime Secret",
            pending: "Create temporary runtime Secret",
            failed: "Could not create temporary runtime Secret");
        AddOptionalCheck(
            checks,
            stage,
            "playwright-job",
            completed: "Playwright cleanup Job created",
            running: "Creating Playwright cleanup Job",
            pending: "Create Playwright cleanup Job",
            failed: "Could not create Playwright cleanup Job");
        var cleanupOperation = FindOperation(stage, "playwright-tests");
        if (cleanupOperation is not null)
        {
            checks.Add(new UiStageCheckResult
            {
                Description = BuildPlaywrightTestsDescription(cleanupOperation, isCleanup: true),
                Status = ToViewModelDetailStatus(cleanupOperation.Status)
            });
        }

        return checks.Count > 0 ? checks : BuildGenericChecks(stage);
    }

    private static List<UiStageCheckResult> BuildRollbackChecks(UpgradeJobStage stage)
    {
        return stage.Operations
            .Select(operation => new UiStageCheckResult
            {
                Description = BuildRollbackOperationDescription(stage.Name, operation),
                Status = ToViewModelDetailStatus(operation.Status)
            })
            .ToList();
    }

    private static List<UiStageCheckResult> BuildGenericChecks(UpgradeJobStage stage)
    {
        return stage.Operations.Select(operation => new UiStageCheckResult
        {
            Description = BuildGenericOperationDescription(operation),
            Status = ToViewModelDetailStatus(operation.Status)
        }).ToList();
    }

    private static List<UiStageCheckResult> BuildSchemaBackupChecks(UpgradeJobStage stage)
    {
        var checks = new List<UiStageCheckResult>();
        var discovery = FindOperation(stage, "database-discovery");
        if (discovery is not null)
        {
            var databaseCount = ExtractNumber(discovery.Message, @"Databases processed:\s*(\d+)");
            checks.Add(new UiStageCheckResult
            {
                Description = BuildSchemaBackupStepDescription(
                    discovery.Status,
                    databaseCount.HasValue
                        ? $"Identified {databaseCount.Value} affected {Pluralize(databaseCount.Value, "database", "databases")}"
                        : "Identified affected databases",
                    "Identifying affected databases",
                    "Identify affected databases",
                    "Could not identify affected databases"),
                Status = ToViewModelDetailStatus(discovery.Status)
            });
        }

        var impact = FindOperation(stage, "database-impact");
        if (impact is not null)
        {
            var (currentVersion, targetVersion) = ExtractVersionRange(impact.Message);
            checks.Add(new UiStageCheckResult
            {
                Description = BuildSchemaBackupStepDescription(
                    impact.Status,
                    !string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(targetVersion)
                        ? $"Analyzed the database impact for the upgrade from {currentVersion} to {targetVersion}"
                        : "Analyzed the database impact for the selected upgrade",
                    "Analyzing the database impact for the selected upgrade",
                    "Analyze the database impact for the selected upgrade",
                    "Could not analyze the database impact"),
                Status = ToViewModelDetailStatus(impact.Status)
            });
        }

        var tableDiscovery = FindOperation(stage, "table-discovery");
        if (tableDiscovery is not null)
        {
            var tableCount = ExtractNumber(tableDiscovery.Message, @"Identified\s+(\d+)\s+affected");
            checks.Add(new UiStageCheckResult
            {
                Description = BuildSchemaBackupStepDescription(
                    tableDiscovery.Status,
                    tableCount.HasValue
                        ? $"Identified {tableCount.Value} affected {Pluralize(tableCount.Value, "table", "tables")}"
                        : "Identified affected tables",
                    "Identifying affected tables",
                    "Identify affected tables",
                    "Could not identify affected tables"),
                Status = ToViewModelDetailStatus(tableDiscovery.Status)
            });
        }

        var backupOperations = stage.Operations
            .Where(operation => operation.Id.StartsWith("database-backup", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (backupOperations.Count > 0)
        {
            checks.Add(new UiStageCheckResult
            {
                Description = BuildSchemaBackupSummary(backupOperations),
                Status = AggregateDetailStatus(backupOperations)
            });
        }

        return checks;
    }

    private static string BuildSchemaBackupStepDescription(
        UpgradeJobStageStatus status,
        string completed,
        string running,
        string pending,
        string failed)
    {
        return BuildStepDescription(status, completed, running, pending, failed);
    }

    private static string BuildStepDescription(
        UpgradeJobStageStatus status,
        string completed,
        string running,
        string pending,
        string failed)
    {
        return status switch
        {
            UpgradeJobStageStatus.Running => running,
            UpgradeJobStageStatus.Pending => pending,
            UpgradeJobStageStatus.Failed => failed,
            _ => completed
        };
    }

    private static void AddOptionalCheck(
        ICollection<UiStageCheckResult> checks,
        UpgradeJobStage stage,
        string operationId,
        string completed,
        string running,
        string pending,
        string failed)
    {
        var operation = FindOperation(stage, operationId);
        if (operation is null)
        {
            return;
        }

        checks.Add(new UiStageCheckResult
        {
            Description = BuildStepDescription(operation.Status, completed, running, pending, failed),
            Status = ToViewModelDetailStatus(operation.Status)
        });
    }

    private static string BuildPlaywrightTestsDescription(UpgradeJobOperation operation, bool isCleanup)
    {
        if (IsProductHealthValidationOperation(operation))
        {
            return operation.Status switch
            {
                UpgradeJobStageStatus.Running => "Running product health validation",
                UpgradeJobStageStatus.Pending => "Run product health validation",
                UpgradeJobStageStatus.Failed => "Product health validation failed",
                UpgradeJobStageStatus.Skipped => "Product health validation was skipped",
                _ => "Product health validation completed"
            };
        }

        var summary = TryParsePlaywrightSummary(operation.SafeErrorDetails ?? operation.Message);
        if (summary is not null)
        {
            if (isCleanup)
            {
                return operation.Status == UpgradeJobStageStatus.Running
                    ? $"Running cleanup checks - {summary.CompletedTests}/{summary.Total} checks completed"
                    : BuildCleanupDetailMessage(summary, operation.Status);
            }

            return operation.Status == UpgradeJobStageStatus.Running
                ? $"Running Playwright validation - {summary.CompletedTests}/{summary.Total} tests completed"
                : BuildPlaywrightSummaryMessage(summary, operation.Status);
        }

        return operation.Status switch
        {
            UpgradeJobStageStatus.Running => isCleanup ? "Running cleanup validation" : "Running Playwright validation",
            UpgradeJobStageStatus.Pending => isCleanup ? "Run cleanup validation" : "Run Playwright validation",
            UpgradeJobStageStatus.Failed => isCleanup
                ? CleanupManualActionMessage
                : "Validation could not be completed due to an execution error. Review the stage details for more information.",
            UpgradeJobStageStatus.Skipped => isCleanup ? "Cleanup validation was skipped" : "Playwright validation was skipped",
            _ => isCleanup ? "Cleanup validation completed" : "Playwright validation completed"
        };
    }

    private static string BuildPlaywrightStageSummary(UpgradeJobStage stage, bool isCleanup)
    {
        var summary = TryParsePlaywrightSummary(stage.SafeErrorDetails ?? stage.Message);
        if (isCleanup)
        {
            return BuildCleanupStageSummary(stage);
        }

        if (summary is not null)
        {
            return BuildPlaywrightSummaryMessage(summary, stage.Status);
        }

        return stage.Status switch
        {
            UpgradeJobStageStatus.Failed when isCleanup =>
                CleanupManualActionMessage,
            UpgradeJobStageStatus.Failed =>
                "Validation could not be completed due to an execution error. Review the stage details for more information.",
            UpgradeJobStageStatus.Skipped when isCleanup => "Cleanup validation was skipped.",
            UpgradeJobStageStatus.Skipped => "Validation was skipped.",
            UpgradeJobStageStatus.Running when isCleanup => "Cleanup validation is in progress.",
            UpgradeJobStageStatus.Running => "Validation is in progress.",
            UpgradeJobStageStatus.Succeeded when isCleanup => "Cleanup validation completed.",
            UpgradeJobStageStatus.Succeeded => "Validation completed.",
            _ => stage.SafeErrorDetails ?? stage.Message ?? "Validation status is not available."
        };
    }

    private static string BuildPlaywrightSummaryMessage(
        PlaywrightSummary summary,
        UpgradeJobStageStatus status)
    {
        const string itemName = "tests";
        const string validationLabel = "Validation";
        var skippedText = summary.Skipped > 0
            ? $", and {summary.Skipped} {Pluralize(summary.Skipped, "was", "were")} skipped"
            : string.Empty;

        if (status == UpgradeJobStageStatus.Failed && summary.Failed == 0 && summary.PassPercentage < summary.Threshold)
        {
            return $"{validationLabel} did not meet the required pass threshold. {summary.Passed} of {summary.Total} {itemName} passed ({summary.PassPercentage:0.##}%; required: {summary.Threshold}%){skippedText}.";
        }

        if (status == UpgradeJobStageStatus.Failed && summary.Failed > 0)
        {
            return $"{validationLabel} failed. {summary.Passed} of {summary.Total} {itemName} passed, {summary.Failed} failed{skippedText}. Pass rate: {summary.PassPercentage:0.##}%; required: {summary.Threshold}%.";
        }

        if (status == UpgradeJobStageStatus.Failed)
        {
            return "Validation could not be completed due to an execution error. Review the stage details for more information.";
        }

        return $"{validationLabel} met the required pass threshold. {summary.Passed} of {summary.Total} {itemName} passed ({summary.PassPercentage:0.##}%; required: {summary.Threshold}%){skippedText}.";
    }

    private static string BuildCleanupStageSummary(UpgradeJobStage stage)
    {
        return stage.Status switch
        {
            UpgradeJobStageStatus.Succeeded => "Cleanup completed successfully.",
            UpgradeJobStageStatus.Running => "Cleanup is in progress.",
            UpgradeJobStageStatus.Skipped => "Cleanup was skipped.",
            UpgradeJobStageStatus.Failed => CleanupManualActionMessage,
            _ => stage.SafeErrorDetails ?? stage.Message ?? "Cleanup status is not available."
        };
    }

    private static string BuildCleanupDetailMessage(PlaywrightSummary summary, UpgradeJobStageStatus status)
    {
        if (status == UpgradeJobStageStatus.Succeeded &&
            summary.Total > 0 &&
            summary.Failed == 0 &&
            summary.Passed + summary.Skipped == summary.Total)
        {
            return summary.Skipped > 0
                ? $"Cleanup checks completed successfully. {summary.Passed} succeeded and {summary.Skipped} were skipped as not applicable."
                : $"Cleanup checks completed successfully. {summary.Passed} of {summary.Total} checks completed.";
        }

        var failedText = summary.Failed > 0 ? $"{summary.Failed} failed" : "0 failed";
        var skippedText = summary.Skipped > 0 ? $"{summary.Skipped} skipped" : "0 skipped";
        return $"{CleanupManualActionMessage} {summary.Passed} succeeded, {failedText}, and {skippedText}.";
    }

    private static bool IsProductHealthValidationOperation(UpgradeJobOperation operation)
    {
        var message = operation.SafeErrorDetails ?? operation.Message ?? string.Empty;
        return message.Contains("product health validation", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Product health checks", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRollbackOperationDescription(UpgradeJobStageName stageName, UpgradeJobOperation operation)
    {
        if (stageName == UpgradeJobStageName.RollbackDatabaseRestore)
        {
            return BuildStepDescription(
                operation.Status,
                "Restored affected database tables",
                "Restoring affected database tables",
                "Restore affected database tables",
                "Could not restore affected database tables");
        }

        if (stageName == UpgradeJobStageName.RollbackImageRevert)
        {
            var deploymentName = ResolveDeploymentName(operation, " image reverted");
            return BuildStepDescription(
                operation.Status,
                $"{deploymentName} image tag reverted",
                $"Reverting image tag for {deploymentName}",
                $"Revert image tag for {deploymentName}",
                $"Could not revert image tag for {deploymentName}");
        }

        if (stageName == UpgradeJobStageName.RollbackRolloutWait)
        {
            var deploymentName = ResolveDeploymentName(operation, " rollout completed");
            return BuildStepDescription(
                operation.Status,
                $"{deploymentName} rollout completed",
                $"Waiting for {deploymentName} rollout",
                $"Wait for {deploymentName} rollout",
                $"{deploymentName} rollout did not complete");
        }

        if (stageName == UpgradeJobStageName.RollbackHealthVerification)
        {
            return BuildHealthCheckDescription(operation);
        }

        if (stageName == UpgradeJobStageName.RollbackComplete)
        {
            return BuildStepDescription(
                operation.Status,
                "Rollback finalized",
                "Finalizing rollback",
                "Finalize rollback",
                "Could not finalize rollback");
        }

        if (stageName == UpgradeJobStageName.AutomaticRollback)
        {
            return BuildAutomaticRollbackDescription(operation);
        }

        return BuildGenericOperationDescription(operation);
    }

    private static string BuildAutomaticRollbackDescription(UpgradeJobOperation operation)
    {
        if (operation.Id.Equals("rollback-database", StringComparison.OrdinalIgnoreCase))
        {
            return BuildStepDescription(
                operation.Status,
                "Restored affected database tables",
                "Restoring affected database tables",
                "Restore affected database tables",
                "Could not restore affected database tables");
        }

        if (operation.Id.Equals("rollback-kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            return BuildStepDescription(
                operation.Status,
                "Previous Kubernetes deployment images restored",
                "Restoring previous Kubernetes deployment images",
                "Restore previous Kubernetes deployment images",
                "Could not restore previous Kubernetes deployment images");
        }

        if (operation.Id.Equals("rollback-health", StringComparison.OrdinalIgnoreCase))
        {
            return BuildStepDescription(
                operation.Status,
                "Product health verified after rollback",
                "Verifying product health after rollback",
                "Verify product health after rollback",
                "Product health verification failed after rollback");
        }

        if (operation.Id.StartsWith("kubernetes-rollback-", StringComparison.OrdinalIgnoreCase))
        {
            var deploymentName = ResolveDeploymentName(operation, null);
            return BuildStepDescription(
                operation.Status,
                $"{deploymentName} image restored and rollout is healthy",
                $"{deploymentName} rollback is in progress",
                $"{deploymentName} rollback is pending",
                $"{deploymentName} rollback failed");
        }

        return BuildHealthCheckDescription(operation);
    }

    private static string BuildHealthCheckDescription(UpgradeJobOperation operation)
    {
        var name = operation.Message?.Split(':', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals(operation.Message, StringComparison.Ordinal))
        {
            name = operation.Name;
        }

        return BuildStepDescription(
            operation.Status,
            $"{name} health verified",
            $"Verifying {name} health",
            $"Verify {name} health",
            $"{name} health verification failed");
    }

    private static string BuildGenericOperationDescription(UpgradeJobOperation operation)
    {
        var name = CleanOperationName(operation.Name);
        return operation.Status switch
        {
            UpgradeJobStageStatus.Running => ToPresentParticiple(name),
            UpgradeJobStageStatus.Pending => name,
            UpgradeJobStageStatus.Failed => $"Could not {LowercaseFirst(name)}",
            _ => name
        };
    }

    private static UpgradeJobOperation? FindOperation(UpgradeJobStage stage, string operationId)
    {
        return stage.Operations.FirstOrDefault(operation =>
            string.Equals(operation.Id, operationId, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSchemaBackupSummary(IReadOnlyList<UpgradeJobOperation> operations)
    {
        if (operations.Any(operation => operation.Status == UpgradeJobStageStatus.Failed))
        {
            return "Could not back up all tenant and master databases";
        }

        if (operations.All(operation => operation.Status == UpgradeJobStageStatus.Skipped))
        {
            return "No physical backup was required for the affected tables";
        }

        if (operations.Any(operation => operation.Status == UpgradeJobStageStatus.Running))
        {
            return "Backing up all tenant and master databases";
        }

        if (operations.Any(operation => operation.Status == UpgradeJobStageStatus.Pending))
        {
            return "Back up all tenant and master databases";
        }

        if (operations.Any(operation => (operation.Message ?? string.Empty).Contains("No physical backup database was required", StringComparison.OrdinalIgnoreCase)))
        {
            return "Completed database backup preparation; some databases did not require a physical backup";
        }

        return "Backed up all tenant and master databases";
    }

    private static UiStageDetailStatus AggregateDetailStatus(IReadOnlyList<UpgradeJobOperation> operations)
    {
        if (operations.Any(operation => operation.Status == UpgradeJobStageStatus.Failed))
        {
            return UiStageDetailStatus.Failed;
        }

        if (operations.Any(operation => operation.Status == UpgradeJobStageStatus.Running))
        {
            return UiStageDetailStatus.Running;
        }

        if (operations.Any(operation => operation.Status == UpgradeJobStageStatus.Pending))
        {
            return UiStageDetailStatus.Pending;
        }

        if (operations.Any(operation => (operation.Message ?? string.Empty).Contains("No physical backup database was required", StringComparison.OrdinalIgnoreCase)))
        {
            return UiStageDetailStatus.Warning;
        }

        if (operations.All(operation => operation.Status == UpgradeJobStageStatus.Skipped))
        {
            return UiStageDetailStatus.Skipped;
        }

        return UiStageDetailStatus.Completed;
    }

    private static int CalculatePlaywrightValidationProgressPercent(UpgradeJobStage stage)
    {
        if (stage.Status == UpgradeJobStageStatus.Skipped)
        {
            return 0;
        }

        if (stage.Status is UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Skipped)
        {
            return 100;
        }

        var testOperation = FindOperation(stage, "playwright-tests")
            ?? FindOperation(stage, stage.Name == UpgradeJobStageName.PostUpgradeValidation ? "post-validation-tests" : "pre-validation-tests");
        var setupOperations = stage.Operations
            .Where(operation =>
                !operation.Id.Equals(testOperation?.Id, StringComparison.OrdinalIgnoreCase) &&
                (operation.Id.StartsWith("playwright-", StringComparison.OrdinalIgnoreCase) ||
                 operation.Id.Equals("post-validation-wait", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var setupProgress = setupOperations.Count == 0
            ? 0
            : setupOperations.Count(operation => HasOperationStarted(operation) && IsTerminalOperation(operation)) * 50.0 / setupOperations.Count;

        var executionProgress = 0.0;
        if (testOperation is not null)
        {
            var summary = TryParsePlaywrightSummary(testOperation.SafeErrorDetails ?? testOperation.Message);
            if (summary is not null && summary.Total > 0)
            {
                executionProgress = Math.Clamp(summary.CompletedTests * 50.0 / summary.Total, 0, 50);
            }
            else if (IsTerminalOperation(testOperation))
            {
                executionProgress = 50;
            }
            else if (testOperation.Status == UpgradeJobStageStatus.Running)
            {
                executionProgress = 0;
            }
        }

        return Math.Clamp((int)Math.Round(setupProgress + executionProgress), 0, 100);
    }

    private static int CalculateDeploymentProgressPercent(UpgradeJobStage stage, string operationPrefix)
    {
        if (stage.Status == UpgradeJobStageStatus.Skipped)
        {
            return 0;
        }

        var deploymentOperations = stage.Operations
            .Where(operation => operation.Id.StartsWith(operationPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deploymentOperations.Count == 0)
        {
            return stage.Status switch
            {
                UpgradeJobStageStatus.Succeeded => 100,
                _ => 0
            };
        }

        var completed = deploymentOperations.Count(operation => HasOperationStarted(operation) && IsTerminalOperation(operation));
        return Math.Clamp((int)Math.Round(completed * 100.0 / deploymentOperations.Count), 0, 100);
    }

    private static int CalculateCleanupProgressPercent(UpgradeJobStage stage)
    {
        if (stage.Status == UpgradeJobStageStatus.Skipped)
        {
            return 0;
        }

        if (stage.Status == UpgradeJobStageStatus.Succeeded)
        {
            return 100;
        }

        var cleanupOperation = FindOperation(stage, "playwright-tests");
        var summary = TryParsePlaywrightSummary(cleanupOperation?.SafeErrorDetails ?? cleanupOperation?.Message);
        if (summary is not null && summary.Total > 0)
        {
            if (stage.Status == UpgradeJobStageStatus.Running)
            {
                return Math.Clamp((int)Math.Round(summary.CompletedTests * 100.0 / summary.Total), 0, 99);
            }

            var executed = summary.Passed + summary.Failed;
            return executed > 0
                ? Math.Clamp((int)Math.Round(summary.Passed * 100.0 / executed), 0, 99)
                : 0;
        }

        if (stage.Operations.Count == 0)
        {
            return 0;
        }

        var successfulOperations = stage.Operations.Count(operation =>
            HasOperationStarted(operation) &&
            operation.Status == UpgradeJobStageStatus.Succeeded);
        return Math.Clamp((int)Math.Round(successfulOperations * 100.0 / stage.Operations.Count), 0, 99);
    }

    private static bool HasStageStarted(UpgradeJobStage stage)
    {
        return stage.StartedAt.HasValue ||
               stage.Status == UpgradeJobStageStatus.Running ||
               stage.Operations.Any(HasOperationStarted);
    }

    private static bool HasOperationStarted(UpgradeJobOperation operation)
    {
        return operation.StartedAt.HasValue;
    }

    private static bool IsTerminalOperation(UpgradeJobOperation operation)
    {
        return operation.Status is UpgradeJobStageStatus.Succeeded
            or UpgradeJobStageStatus.Skipped
            or UpgradeJobStageStatus.Failed
            or UpgradeJobStageStatus.Cancelled;
    }

    private static PlaywrightSummary? TryParsePlaywrightSummary(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var total = ExtractNumber(message, @"Total:\s*(\d+)");
        var passed = ExtractNumber(message, @"Passed:\s*(\d+)");
        var failed = ExtractNumber(message, @"Failed:\s*(\d+)");
        var skipped = ExtractNumber(message, @"Skipped:\s*(\d+)");
        var threshold = ExtractNumber(message, @"Threshold:\s*(\d+)");
        var passPercentage = ExtractDouble(message, @"Pass percentage:\s*([0-9]+(?:\.[0-9]+)?)%");
        var finalStatus = ExtractBetween(message, "Final status:", "Reason:")
            ?? ExtractAfter(message, "Final status:");

        if (!total.HasValue || !passed.HasValue || !failed.HasValue || !skipped.HasValue)
        {
            var readableMatch = System.Text.RegularExpressions.Regex.Match(
                message,
                @"(?<passed>\d+)\s+of\s+(?<total>\d+)\s+(?:tests|checks)\s+passed.*?(?:,\s+(?<failed>\d+)\s+failed)?(?:.*?(?<skipped>\d+)\s+(?:was|were)\s+skipped)?.*?(?:required:\s*(?<threshold>\d+)%)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (readableMatch.Success)
            {
                passed = int.Parse(readableMatch.Groups["passed"].Value, System.Globalization.CultureInfo.InvariantCulture);
                total = int.Parse(readableMatch.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture);
                failed = readableMatch.Groups["failed"].Success
                    ? int.Parse(readableMatch.Groups["failed"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                skipped = readableMatch.Groups["skipped"].Success
                    ? int.Parse(readableMatch.Groups["skipped"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                threshold = readableMatch.Groups["threshold"].Success
                    ? int.Parse(readableMatch.Groups["threshold"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 100;
                passPercentage ??= CalculatePassPercentage(passed.Value, total.Value);
            }
        }

        return total.HasValue && passed.HasValue && failed.HasValue && skipped.HasValue
            ? new PlaywrightSummary(
                total.Value,
                passed.Value,
                failed.Value,
                skipped.Value,
                passPercentage ?? CalculatePassPercentage(passed.Value, total.Value),
                threshold ?? 100,
                string.IsNullOrWhiteSpace(finalStatus) ? "Unknown" : finalStatus.Trim().TrimEnd('.'))
            : null;
    }

    private static double CalculatePassPercentage(int passed, int total)
    {
        return total <= 0 ? 0 : Math.Round(passed * 100.0 / total, 2);
    }

    private static int? ExtractNumber(string? message, string pattern)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(message, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static double? ExtractDouble(string? message, string pattern)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(message, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string Pluralize(int count, string singular, string plural)
    {
        return count == 1 ? singular : plural;
    }

    private static (string? CurrentVersion, string? TargetVersion) ExtractVersionRange(string? message)
    {
        return (
            ExtractBetween(message, "Current version:", "Target version:"),
            ExtractBetween(message, "Target version:", "Versions analyzed:"));
    }

    private static string? ExtractBetween(string? message, string startLabel, string? endLabel)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var start = message.IndexOf(startLabel, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += startLabel.Length;
        var end = endLabel is null
            ? -1
            : message.IndexOf(endLabel, start, StringComparison.OrdinalIgnoreCase);
        var value = end >= 0
            ? message[start..end]
            : message[start..];

        return value.Trim().TrimEnd('.');
    }

    private static string? ExtractAfter(string? message, string startLabel)
    {
        return ExtractBetween(message, startLabel, null);
    }

    private static string ResolveDeploymentName(UpgradeJobOperation operation, string? suffixToRemove)
    {
        if (!string.IsNullOrWhiteSpace(operation.Name))
        {
            var name = operation.Name.Trim();
            if (!string.IsNullOrWhiteSpace(suffixToRemove) && name.EndsWith(suffixToRemove, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffixToRemove.Length].Trim();
            }

            if (!name.Contains(' ', StringComparison.Ordinal))
            {
                return name;
            }
        }

        var id = operation.Id;
        foreach (var prefix in new[] { "kubernetes-deployment-", "kubernetes-rollback-", "rollback-image-", "rollback-rollout-" })
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return id[prefix.Length..];
            }
        }

        return operation.Name;
    }

    private static string CleanOperationName(string value)
    {
        var name = value.Trim().TrimEnd('.');
        return name
            .Replace("Started ", "Start ", StringComparison.OrdinalIgnoreCase)
            .Replace("Executed ", "Execute ", StringComparison.OrdinalIgnoreCase)
            .Replace("Completed ", "Complete ", StringComparison.OrdinalIgnoreCase)
            .Replace("Restored ", "Restore ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToPresentParticiple(string value)
    {
        if (value.StartsWith("Create ", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating " + value[7..];
        }

        if (value.StartsWith("Execute ", StringComparison.OrdinalIgnoreCase))
        {
            return "Executing " + value[8..];
        }

        if (value.StartsWith("Start ", StringComparison.OrdinalIgnoreCase))
        {
            return "Starting " + value[6..];
        }

        if (value.StartsWith("Restore ", StringComparison.OrdinalIgnoreCase))
        {
            return "Restoring " + value[8..];
        }

        if (value.StartsWith("Verify ", StringComparison.OrdinalIgnoreCase))
        {
            return "Verifying " + value[7..];
        }

        if (value.StartsWith("Wait ", StringComparison.OrdinalIgnoreCase))
        {
            return "Waiting " + value[5..];
        }

        return value;
    }

    private static string LowercaseFirst(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("dd MMM yyyy, HH:mm:ss 'UTC'") ?? "Not available";

    private sealed record PlaywrightSummary(
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        double PassPercentage,
        int Threshold,
        string FinalStatus)
    {
        public int CompletedTests => Math.Clamp(Passed + Failed + Skipped, 0, Total);
    }
}
