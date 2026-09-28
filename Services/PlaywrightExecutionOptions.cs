namespace Bold.UpgradeCenter.Services;

public sealed class PlaywrightExecutionOptions
{
    public bool UseKubernetesJob { get; set; }

    public int JobTimeoutSeconds { get; set; } = 9000;

    public int CleanupJobTimeoutSeconds { get; set; } = 900;

    public int JobPollSeconds { get; set; } = 5;

    public int BackoffLimit { get; set; }

    public int? CompletedJobTtlSeconds { get; set; } = 3600;

    public int WorkerCount { get; set; } = 1;

    public string? JobCpuRequest { get; set; } = "1";

    public string? JobCpuLimit { get; set; } = "2";

    public string? JobMemoryRequest { get; set; } = "2Gi";

    public string? JobMemoryLimit { get; set; } = "4Gi";

    public bool SharedStateVolumeEnabled { get; set; } = true;

    public string? SharedStateStorageClassName { get; set; }

    public string SharedStateStorageSize { get; set; } = "1Gi";

    public string SharedStateAccessMode { get; set; } = "ReadWriteOnce";

    public string SharedStateMountPath { get; set; } = "/app/playwright-shared-state";

    public int? SharedStateFsGroup { get; set; } = 1001;

    public string? PassRateThreshold { get; set; } = "100";

    public string ReportPath { get; set; } = "/app/test-results/reports.html";

    public string PreJunitReportPath { get; set; } = "/app/pre-upgrade-test-results/junit-report.xml";

    public string PostJunitReportPath { get; set; } = "/app/post-upgrade-test-results/junit-report.xml";

    public string PreHtmlReportPath { get; set; } = "/app/pre-upgrade-test-results/reports.html";

    public string PostHtmlReportPath { get; set; } = "/app/post-upgrade-test-results/reports.html";

    public string PreHtmlReportDirectory { get; set; } = "/app/pre-upgrade-test-results";

    public string PostHtmlReportDirectory { get; set; } = "/app/post-upgrade-test-results";

    public string? ReportStoragePath { get; set; }

    public string? ReportUploadBaseUrl { get; set; }

    public string? ResultUploadBaseUrl { get; set; }

    public int ReportRetentionJobCount { get; set; } = 7;

    public string? AdminUsername { get; set; }

    public string? AdminPassword { get; set; }

    public string? DbHost { get; set; }

    public string? DbPort { get; set; }

    public string? DbType { get; set; }

    public string? DbUsername { get; set; }

    public string? DbPassword { get; set; }

    public string? DbMaintenance { get; set; }

    public string? DbAdditionalParameters { get; set; }

    public bool? DbSsl { get; set; }
}

public enum PlaywrightValidationMode
{
    Pre,
    Post,
    Cleanup
}

public sealed record PlaywrightValidationProgress(
    string OperationId,
    UpgradeJobStageStatus Status,
    string Message);
