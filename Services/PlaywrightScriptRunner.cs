using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class PlaywrightScriptRunner : IPlaywrightScriptRunner
{
    private readonly IPlaywrightKubernetesJobService kubernetesJobService;
    private readonly IProductHealthCheckService productHealthCheckService;
    private readonly ILogger<PlaywrightScriptRunner> logger;
    private readonly PlaywrightExecutionOptions options;

    public PlaywrightScriptRunner(
        IPlaywrightKubernetesJobService kubernetesJobService,
        IProductHealthCheckService productHealthCheckService,
        IOptions<PlaywrightExecutionOptions> options,
        ILogger<PlaywrightScriptRunner> logger)
    {
        this.kubernetesJobService = kubernetesJobService;
        this.productHealthCheckService = productHealthCheckService;
        this.options = options.Value;
        this.logger = logger;
    }

    public Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteProductHealthValidationAsync(PlaywrightValidationMode.Pre, progress: null, cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteProductHealthValidationAsync(PlaywrightValidationMode.Post, progress: null, cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default)
    {
        return options.UseKubernetesJob
            ? kubernetesJobService.RunAsync(PlaywrightValidationMode.Pre, upgradeJobId, progress, runnerImageVersion, cancellationToken)
            : ExecuteProductHealthValidationAsync(PlaywrightValidationMode.Pre, progress, cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default)
    {
        return options.UseKubernetesJob
            ? kubernetesJobService.RunAsync(PlaywrightValidationMode.Post, upgradeJobId, progress, runnerImageVersion, cancellationToken)
            : ExecuteProductHealthValidationAsync(PlaywrightValidationMode.Post, progress, cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecuteCleanupAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default)
    {
        return options.UseKubernetesJob
            ? kubernetesJobService.RunCleanupAsync(upgradeJobId, progress, runnerImageVersion, cancellationToken)
            : ExecuteSkippedCleanupAsync(progress);
    }

    public Task CleanupSharedStateAsync(string upgradeJobId, CancellationToken cancellationToken = default)
    {
        return options.UseKubernetesJob
            ? kubernetesJobService.CleanupSharedStateAsync(upgradeJobId, cancellationToken)
            : Task.CompletedTask;
    }

    private async Task<PlaywrightScriptResult> ExecuteProductHealthValidationAsync(
        PlaywrightValidationMode mode,
        Action<PlaywrightValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var scriptName = mode == PlaywrightValidationMode.Pre
            ? "Pre-upgrade product health validation"
            : "Post-upgrade product health validation";
        const string command = "ProductHealthCheckService.VerifyAsync";

        logger.LogInformation("Starting {ScriptName} because the Playwright Kubernetes runner is disabled.", scriptName);

        try
        {
            progress?.Invoke(new PlaywrightValidationProgress(
                "product-health-validation",
                UpgradeJobStageStatus.Running,
                "Running product health validation."));

            var targets = productHealthCheckService.GetRollbackHealthCheckTargets(Array.Empty<string>());
            var result = await productHealthCheckService.VerifyAsync(
                targets,
                checkProgress => progress?.Invoke(new PlaywrightValidationProgress(
                    checkProgress.Target.OperationId,
                    checkProgress.Status,
                    $"{checkProgress.Target.Name}: {checkProgress.Message}")),
                cancellationToken);

            var output = string.Join(
                Environment.NewLine,
                result.Checks.Select(check => $"{check.Target.Name}: {check.Status}. {check.Message}"));

            var message = result.Succeeded
                ? $"{scriptName} completed successfully. {result.Message}"
                : $"{scriptName} failed. {result.Message}";

            if (result.Succeeded)
            {
                logger.LogInformation("{ScriptName} completed successfully.", scriptName);
            }
            else
            {
                logger.LogWarning("{ScriptName} failed. {Message}", scriptName, result.Message);
            }

            progress?.Invoke(new PlaywrightValidationProgress(
                "product-health-validation",
                result.Succeeded ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                message));

            return CreateResult(result.Succeeded, result.Succeeded ? 0 : 1, false, startedAt, command, output, string.Empty, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{ScriptName} was cancelled.", scriptName);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error while running {ScriptName}.", scriptName);
            return CreateResult(false, 1, false, startedAt, command, string.Empty, exception.Message, $"Unexpected error while running {scriptName}.");
        }
    }

    private static Task<PlaywrightScriptResult> ExecuteSkippedCleanupAsync(Action<PlaywrightValidationProgress>? progress)
    {
        var startedAt = DateTimeOffset.UtcNow;
        const string message = "Playwright cleanup skipped because the Kubernetes Playwright runner is disabled.";
        return Task.FromResult(CreateResult(true, 0, false, startedAt, "Playwright cleanup", string.Empty, string.Empty, message));
    }

    private static PlaywrightScriptResult CreateResult(
        bool succeeded,
        int? exitCode,
        bool timedOut,
        DateTimeOffset startedAt,
        string command,
        string standardOutput,
        string standardError,
        string message)
    {
        return new PlaywrightScriptResult(
            succeeded,
            exitCode,
            timedOut,
            startedAt,
            DateTimeOffset.UtcNow,
            command,
            standardOutput,
            standardError,
            message);
    }
}
