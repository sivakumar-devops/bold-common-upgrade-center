namespace Bold.UpgradeCenter.Services;

public enum KubernetesUpgradeScope
{
    Bulk,
    Individual
}

public sealed class KubernetesUpgradeOptions
{
    public bool Enabled { get; set; } = true;

    public string? Namespace { get; set; }

    public string? ApiServer { get; set; }

    public string ServiceAccountTokenPath { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    public string NamespacePath { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    public string CertificateAuthorityPath { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

    public bool SkipTlsVerify { get; set; }

    public bool AllowSkipTlsVerifyForLocalDevelopment { get; set; }

    public int RolloutTimeoutSeconds { get; set; } = 300;

    public int RolloutPollSeconds { get; set; } = 5;

    public List<string> ExpectedDeployments { get; set; } = [];

    public List<string> OptionalDeployments { get; set; } = [];

    public List<string> AllowedImageRepositories { get; set; } = [];

    public List<string> AllowedImageRegistryHosts { get; set; } = [];
}

public sealed record KubernetesUpgradeRequest(
    KubernetesUpgradeScope Scope,
    string? DeploymentName);

public sealed record KubernetesNamespaceValidationResult(
    bool Succeeded,
    string Namespace,
    string Message,
    IReadOnlyList<string> ExpectedDeployments,
    IReadOnlyList<string> AvailableDeployments,
    IReadOnlyList<string> MatchedDeployments,
    IReadOnlyList<string> MissingDeployments,
    IReadOnlyList<string> RequiredDeployments,
    IReadOnlyList<string> OptionalDeployments,
    IReadOnlyList<string> MissingRequiredDeployments,
    IReadOnlyList<string> MissingOptionalDeployments,
    IReadOnlyList<KubernetesDeploymentDiscoveryItem> DeploymentStatuses);

public sealed record KubernetesDeploymentDiscoveryItem(
    string DeploymentName,
    bool IsExpected,
    bool IsAvailable,
    bool IsRequired,
    string RuntimeStatus,
    string? RuntimeStatusDetail,
    IReadOnlyList<KubernetesDeploymentContainerInfo> Containers)
{
    public bool IsOptional => IsExpected && !IsRequired;

    public string Availability => IsAvailable ? RuntimeStatus : "Missing";

    public string Requirement => IsExpected
        ? IsRequired ? "Required" : "Optional"
        : "Unsupported";
}

public sealed record KubernetesDeploymentContainerInfo(
    string Name,
    string Image);

public sealed record KubernetesDeploymentImageInfo(
    string DeploymentName,
    string Image,
    string? ContainerName = null);

public sealed record CustomPatchImageMapping(
    string DeploymentName,
    string Image,
    string? ContainerName = null);

public sealed record KubernetesDeploymentImageSnapshot(
    string DeploymentName,
    string ContainerName,
    string Image);

public enum KubernetesDeploymentProgressKind
{
    Upgrade,
    Rollback
}

public enum KubernetesDeploymentProgressStatus
{
    RecordingPreviousImage,
    Patching,
    WaitingForRollout,
    Completed,
    Failed,
    RollingBack,
    RollbackWaitingForRollout,
    RollbackCompleted,
    RollbackFailed
}

public sealed record KubernetesDeploymentProgress(
    KubernetesDeploymentProgressKind Kind,
    string DeploymentName,
    KubernetesDeploymentProgressStatus Status,
    string Message);

public sealed record KubernetesDeploymentUpgradeResult(
    bool Succeeded,
    string DeploymentName,
    string? ContainerName,
    string? PreviousImage,
    string? NewImage,
    string Message);

public sealed record KubernetesUpgradeResult(
    bool Succeeded,
    bool Skipped,
    string Message,
    string? Namespace,
    KubernetesUpgradeScope Scope,
    string? SelectedDeployment,
    IReadOnlyList<KubernetesDeploymentUpgradeResult> DeploymentResults,
    IReadOnlyList<KubernetesDeploymentImageSnapshot> PreviousImages,
    KubernetesNamespaceValidationResult? NamespaceValidation)
{
    public static KubernetesUpgradeResult CreateSkipped(string message)
    {
        return new KubernetesUpgradeResult(
            true,
            true,
            message,
            null,
            KubernetesUpgradeScope.Bulk,
            null,
            Array.Empty<KubernetesDeploymentUpgradeResult>(),
            Array.Empty<KubernetesDeploymentImageSnapshot>(),
            null);
    }

    public static KubernetesUpgradeResult Failed(string message, KubernetesUpgradeScope scope, string? selectedDeployment = null, KubernetesNamespaceValidationResult? validation = null)
    {
        return new KubernetesUpgradeResult(
            false,
            false,
            message,
            validation?.Namespace,
            scope,
            selectedDeployment,
            Array.Empty<KubernetesDeploymentUpgradeResult>(),
            Array.Empty<KubernetesDeploymentImageSnapshot>(),
            validation);
    }
}

public sealed record KubernetesDeploymentRollbackResult(
    bool Succeeded,
    string DeploymentName,
    string ContainerName,
    string PreviousImage,
    string Message);

public sealed record KubernetesRollbackResult(
    bool Succeeded,
    bool Skipped,
    string Message,
    string? Namespace,
    IReadOnlyList<KubernetesDeploymentRollbackResult> DeploymentResults)
{
    public static KubernetesRollbackResult CreateSkipped(string message)
    {
        return new KubernetesRollbackResult(true, true, message, null, Array.Empty<KubernetesDeploymentRollbackResult>());
    }
}
