namespace Bold.UpgradeCenter.Services;

public interface IKubernetesUpgradeService
{
    Task<KubernetesNamespaceValidationResult> ValidateNamespaceAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, KubernetesDeploymentImageInfo>> GetReleaseImagesAsync(
        string selectedVersion,
        CancellationToken cancellationToken = default);

    Task<KubernetesUpgradeResult> UpgradeAsync(
        string selectedVersion,
        KubernetesUpgradeScope scope,
        string? deploymentName,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null,
        Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared = null);

    Task<KubernetesUpgradeResult> UpgradeWithCustomImagesAsync(
        string selectedVersion,
        IReadOnlyList<CustomPatchImageMapping> images,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null,
        Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared = null);

    Task<KubernetesRollbackResult> RollbackAsync(
        KubernetesUpgradeResult upgradeResult,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null);

    Task<KubernetesRollbackResult> ReconcileRollbackAsync(
        KubernetesUpgradeResult upgradeResult,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null);

    Task<KubernetesUpgradeResult> ReconcileUpgradeRolloutAsync(
        KubernetesUpgradeResult upgradeResult,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null);
}
