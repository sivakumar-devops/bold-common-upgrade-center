namespace Bold.UpgradeCenter.Services;

public interface IPlaywrightKubernetesJobService
{
    Task<PlaywrightScriptResult> RunAsync(
        PlaywrightValidationMode mode,
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default);

    Task<PlaywrightScriptResult> RunCleanupAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default);

    Task CleanupSharedStateAsync(string upgradeJobId, CancellationToken cancellationToken = default);
}
