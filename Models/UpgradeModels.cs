using System;
using System.Collections.Generic;

namespace Bold.UpgradeCenter.Models
{
    // Enumerations

    public enum UpgradeJobStatus
    {
        Pending,
        Running,
        Cancelling,
        Cancelled,
        Completed,
        Failed,
        RolledBack,
        RollingBack
    }

    public enum StageStatus
    {
        Pending,
        Running,
        Passed,
        Failed,
        Cancelled,
        Skipped
    }

    public enum StageDetailStatus
    {
        Pending,
        Running,
        Completed,
        Skipped,
        Warning,
        Failed
    }

    public enum ReleaseType
    {
        Standard,
        CustomPatch
    }

    // Core domain models

    /// <summary>
    /// Represents a Bold BI release available for upgrade.
    /// </summary>
    public class Release
    {
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Date the release was published to the update catalog.
        /// </summary>
        public DateTime ReleasedOn { get; set; }

        /// <summary>
        /// URL to the public release notes page.
        /// </summary>
        public string? ReleaseNotesUrl { get; set; }

        /// <summary>
        /// Whether this release is the recommended next step from the currently
        /// installed version (i.e. a direct, sequential upgrade path).
        /// </summary>
        public bool IsRecommendedNext { get; set; }

        /// <summary>
        /// When true the UI shows the version but disables the Upgrade button
        /// and explains the prerequisite version needed first.
        /// </summary>
        public bool RequiresIntermediateUpgrade { get; set; }

        /// <summary>
        /// Human-readable explanation when RequiresIntermediateUpgrade is true.
        /// e.g. "Requires 12.4.x first"
        /// </summary>
        public string? UpgradePathNote { get; set; }

        /// <summary>
        /// True when this release contains schema migrations.  The confirm
        /// dialog shows an automatic-backup notice when this is set.
        /// </summary>
        public bool HasSchemaChanges { get; set; }

        public ReleaseType Type { get; set; } = ReleaseType.Standard;
    }

    /// <summary>
    /// Result of validating a custom patch image reference entered by the admin.
    /// </summary>
    public class PatchValidationResult
    {
        public bool IsValid { get; set; }
        public string ImageReference { get; set; } = string.Empty;
        public string? ImageRepository { get; set; }

        /// <summary>
        /// Human-readable reason when IsValid = false.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Derived version string parsed from the image tag, when valid.
        /// </summary>
        public string? ParsedVersion { get; set; }

        public List<PatchImageValidationItem> Images { get; set; } = [];
    }

    public class PatchImageValidationItem
    {
        public string DeploymentName { get; set; } = string.Empty;
        public string? ContainerName { get; set; }
        public string Image { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    // Upgrade job & stages

    /// <summary>
    /// A single named step (pre-upgrade, schema backup, deploy, etc.)
    /// within an upgrade or rollback job.
    /// </summary>
    public class JobStage
    {
        public string Name { get; set; } = string.Empty;
        public string? Meta { get; set; }
        public StageStatus Status { get; set; } = StageStatus.Pending;
        public int ProgressPercent { get; set; }
        public TimeSpan? Duration { get; set; }
        public List<StageCheckResult> Checks { get; set; } = new();
        public List<string> LogLines { get; set; } = new();
        public string? HtmlReportUrl { get; set; }
    }

    /// <summary>
    /// Individual check within a stage (used in the expandable stage body).
    /// </summary>
    public class StageCheckResult
    {
        public string Description { get; set; } = string.Empty;
        public StageDetailStatus Status { get; set; } = StageDetailStatus.Pending;
        public bool Passed => Status is StageDetailStatus.Completed or StageDetailStatus.Skipped;
    }

    public class OperationLogLine
    {
        public long Sequence { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public string Level { get; set; } = "Info";
        public string Stage { get; set; } = "Job";
        public string Source { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsTruncated { get; set; }
    }

    /// <summary>
    /// The full record of an upgrade (or rollback) execution.
    /// </summary>
    public class UpgradeJob
    {
        public string JobId { get; set; } = string.Empty;
        public string FromVersion { get; set; } = string.Empty;
        public string ToVersion { get; set; } = string.Empty;
        public UpgradeJobStatus Status { get; set; } = UpgradeJobStatus.Pending;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string InitiatedBy { get; set; } = string.Empty;
        public ReleaseType ReleaseType { get; set; } = ReleaseType.Standard;

        public string ProductName { get; set; } = "Bold BI";

        /// <summary>
        /// Ordered stages for this job (pre-upgrade -> upgrade -> post-upgrade,
        /// or revert deployment -> restore schema -> verify health for rollback).
        /// </summary>
        public List<JobStage> Stages { get; set; } = new();

        /// <summary>
        /// Safe, customer-viewable operation log assembled from persisted job
        /// stages and operation messages. Sensitive values must never be added.
        /// </summary>
        public List<string> CompleteLogLines { get; set; } = new();

        public List<OperationLogLine> CompleteLogEntries { get; set; } = new();

        public long CompleteLogSequence { get; set; }

        /// <summary>
        /// Set when this job is a rollback; references the original upgrade job id.
        /// </summary>
        public string? RevertsJobId { get; set; }

        /// <summary>
        /// Populated after a successful upgrade; identifies the associated rollback
        /// job if one has been triggered within the 7-day window.
        /// </summary>
        public string? RollbackJobId { get; set; }

        public bool CancellationRequested { get; set; }

        public string? CancellationMessage { get; set; }

        // Convenience helpers.

        public bool IsRollback => RevertsJobId != null;

        public JobStage? CurrentStage =>
            Stages.Find(s => s.Status is StageStatus.Running or StageStatus.Failed or StageStatus.Cancelled);

        public bool CanRollBack =>
            Status == UpgradeJobStatus.Completed
            && !IsRollback
            && RollbackJobId == null
            && (DateTime.UtcNow - (CompletedAt ?? DateTime.UtcNow)).TotalDays <= 7;
    }

    // History entry (lightweight, for the History tab)

    public class UpgradeHistoryEntry
    {
        public string JobId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;   // e.g. "v12.2.0 -> v12.3.1"
        public string InitiatedBy { get; set; } = string.Empty;
        public UpgradeJobStatus Status { get; set; }
        public DateTime CompletedAt { get; set; }
        public bool HasJobContext { get; set; }
        public bool IsAutomaticRollback { get; set; }
    }

    // Installation / environment info

    public class InstallationInfo
    {
        /// <summary>
        /// Version currently running in the cluster / host.
        /// </summary>
        public string InstalledVersion { get; set; } = string.Empty;

        /// <summary>
        /// The highest version available in the catalog.
        /// </summary>
        public string LatestAvailableVersion { get; set; } = string.Empty;

        /// <summary>
        /// Count of releases newer than the installed version.
        /// </summary>
        public int NewerVersionCount { get; set; }

        /// <summary>
        /// Deployment target descriptor shown in the UI eyebrow.
        /// e.g. "On-premise / Kubernetes"
        /// </summary>
        public string DeploymentTarget { get; set; } = string.Empty;
    }

    // Current user / session context

    public class UpgradeCenterUser
    {
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Two-letter initials shown in the avatar chip.
        /// </summary>
        public string Initials { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// True when the user holds the UMS Admin role and is allowed to
        /// access the Upgrade Center.
        /// </summary>
        public bool IsAuthorized { get; set; }
    }
}
