using Bold.UpgradeCenter.Models;
using UiUpgradeJob = Bold.UpgradeCenter.Models.UpgradeJob;

namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeWorkflowService : IUpgradeService
{
    private readonly IUpgradeJobStore jobStore;
    private readonly IUpgradeJobRunner jobRunner;
    private readonly IUpgradeRollbackStore rollbackStore;
    private readonly IUpgradeRollbackJobRunner rollbackJobRunner;
    private readonly IUpgradeJobCancellationManager cancellationManager;
    private readonly IInstallationInfoProvider installationInfoProvider;
    private readonly IReleaseVersionProvider releaseVersionProvider;
    private readonly IUpgradeHistoryStore historyStore;
    private readonly IUpgradeUserContextProvider userContextProvider;
    private readonly ICustomPatchValidationService customPatchValidationService;
    private readonly IUpgradeEnvironmentEligibilityService upgradeEnvironmentEligibilityService;
    private readonly IUpgradeProductContext productContext;
    private readonly ILogger<UpgradeWorkflowService> logger;

    public UpgradeWorkflowService(
        IUpgradeJobStore jobStore,
        IUpgradeJobRunner jobRunner,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeRollbackJobRunner rollbackJobRunner,
        IUpgradeJobCancellationManager cancellationManager,
        IInstallationInfoProvider installationInfoProvider,
        IReleaseVersionProvider releaseVersionProvider,
        IUpgradeHistoryStore historyStore,
        IUpgradeUserContextProvider userContextProvider,
        ICustomPatchValidationService customPatchValidationService,
        IUpgradeEnvironmentEligibilityService upgradeEnvironmentEligibilityService,
        IUpgradeProductContext productContext,
        ILogger<UpgradeWorkflowService> logger)
    {
        this.jobStore = jobStore;
        this.jobRunner = jobRunner;
        this.rollbackStore = rollbackStore;
        this.rollbackJobRunner = rollbackJobRunner;
        this.cancellationManager = cancellationManager;
        this.installationInfoProvider = installationInfoProvider;
        this.releaseVersionProvider = releaseVersionProvider;
        this.historyStore = historyStore;
        this.userContextProvider = userContextProvider;
        this.customPatchValidationService = customPatchValidationService;
        this.upgradeEnvironmentEligibilityService = upgradeEnvironmentEligibilityService;
        this.productContext = productContext;
        this.logger = logger;
    }

    public async Task<List<Release>> GetAvailableReleasesAsync(string installedVersion, CancellationToken ct = default)
    {
        var summary = await releaseVersionProvider.GetReleaseVersionsAsync(installedVersion, ct);
        var ordered = summary.Versions
            .OrderBy(version => ProductVersionComparer.Parse(version.Version), ProductVersionPartsComparer.Instance)
            .ToList();
        var recommendedNext = ordered.FirstOrDefault()?.Version;

        return summary.Versions
            .Select(version => new Release
            {
                Version = version.Version,
                ReleasedOn = version.PublishedAt?.UtcDateTime ?? DateTime.MinValue,
                ReleaseNotesUrl = version.Url,
                IsRecommendedNext = string.Equals(version.Version, recommendedNext, StringComparison.OrdinalIgnoreCase),
                RequiresIntermediateUpgrade = false,
                HasSchemaChanges = false,
                Type = ReleaseType.Standard
            })
            .ToList();
    }

    public async Task<PatchValidationResult> ValidatePatchImageAsync(
        string patchVersion,
        string? imageRepository = null,
        CancellationToken ct = default)
    {
        var currentVersion = await installationInfoProvider.GetInstallationInfoAsync(ct);
        var validation = await customPatchValidationService.ValidateAsync(
            new CustomPatchValidationRequest
            {
                TargetVersion = patchVersion,
                ImageRepository = imageRepository
            },
            currentVersion.InstalledVersion,
            ct);

        return new PatchValidationResult
        {
            IsValid = validation.IsValid,
            ImageReference = string.Join(", ", validation.Results.Select(result => result.Image)),
            ImageRepository = imageRepository,
            ParsedVersion = validation.TargetVersion,
            ErrorMessage = validation.Errors.Count > 0 ? string.Join(" ", validation.Errors) : null,
            Images = validation.Results.Select(result => new PatchImageValidationItem
            {
                DeploymentName = result.DeploymentName,
                ContainerName = result.ContainerName,
                Image = result.Image,
                IsValid = result.IsValid,
                Message = result.Message
            }).ToList()
        };
    }

    public Task<string?> GetDefaultCustomPatchRepositoryAsync(CancellationToken ct = default)
    {
        return customPatchValidationService.GetCommonImageRepositoryAsync(ct);
    }

    public async Task<UiUpgradeJob> StartUpgradeAsync(string targetVersion, string initiatedBy, CancellationToken ct = default)
    {
        if (jobStore.TryGetLatestActive(out var active))
        {
            throw new InvalidOperationException($"Another upgrade or rollback operation is already running. Active job: {active.Id}.");
        }

        await upgradeEnvironmentEligibilityService.EnsureUpgradeSupportedAsync(ct);
        var currentVersion = await installationInfoProvider.GetInstallationInfoAsync(ct);
        var user = userContextProvider.GetCurrentUser();
        var product = productContext.Current;
        var job = jobStore.Create(
            currentVersion.InstalledVersion,
            targetVersion,
            UpgradeJobType.AvailableRelease,
            [$"All applicable {product.DisplayName} deployments"],
            null,
            user,
            product.Key);

        await historyStore.SaveStartedAsync(CreateHistoryRecord(job), ct);
        jobRunner.Start(job.Id);
        return UpgradeJobViewModelMapper.ToViewModel(job);
    }

    public async Task<UiUpgradeJob> StartCustomPatchUpgradeAsync(
        string patchVersion,
        string? imageRepository,
        string initiatedBy,
        CancellationToken ct = default)
    {
        if (jobStore.TryGetLatestActive(out var active))
        {
            throw new InvalidOperationException($"Another upgrade or rollback operation is already running. Active job: {active.Id}.");
        }

        await upgradeEnvironmentEligibilityService.EnsureUpgradeSupportedAsync(ct);
        var currentVersion = await installationInfoProvider.GetInstallationInfoAsync(ct);
        var product = productContext.Current;
        var validation = await customPatchValidationService.ValidateAsync(
            new CustomPatchValidationRequest
            {
                TargetVersion = patchVersion,
                ImageRepository = imageRepository
            },
            currentVersion.InstalledVersion,
            ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Custom patch validation failed. {string.Join(" ", validation.Errors)}");
        }

        var customImages = validation.Results
            .Where(result => result.IsValid)
            .Select(result => new CustomPatchImageMapping(result.DeploymentName, result.Image, result.ContainerName))
            .ToArray();
        var user = userContextProvider.GetCurrentUser();
        var job = jobStore.Create(
            currentVersion.InstalledVersion,
            validation.TargetVersion,
            UpgradeJobType.CustomVersionOrPatch,
            customImages.Select(image => image.DeploymentName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            customImages,
            user,
            product.Key);

        await historyStore.SaveStartedAsync(CreateHistoryRecord(job), ct);
        jobRunner.Start(job.Id);
        return UpgradeJobViewModelMapper.ToViewModel(job);
    }

    public async Task<UiUpgradeJob> CancelUpgradeAsync(string jobId, string requestedBy, CancellationToken ct = default)
    {
        if (!jobStore.TryGet(jobId, out var job))
        {
            throw new InvalidOperationException("Upgrade job was not found.");
        }

        if (job.UpgradeType == UpgradeJobType.Rollback)
        {
            throw new InvalidOperationException("Rollback operations cannot be cancelled because they are restoring the system to a safe state.");
        }

        if (job.Status is not (UpgradeJobStatus.Queued or UpgradeJobStatus.Running or UpgradeJobStatus.Cancelling))
        {
            throw new InvalidOperationException("This upgrade job is already complete and cannot be cancelled.");
        }

        if (job.CurrentStage == UpgradeJobStageName.AutomaticRollback)
        {
            throw new InvalidOperationException("Cancellation is not available while automatic rollback is running.");
        }

        if (job.CurrentStage == UpgradeJobStageName.CleanupJob)
        {
            throw new InvalidOperationException("Cancellation is not available while the Playwright cleanup job is running.");
        }

        var message = BuildCancellationRequestMessage(job);
        jobStore.RequestCancellation(jobId, requestedBy, message);
        await TryUpdateHistoryStatusAsync(jobId, UpgradeJobStatus.Cancelling.ToString(), ct);
        var cancellationSignaled = cancellationManager.RequestCancellation(jobId);
        if (!cancellationSignaled)
        {
            jobRunner.Recover(jobId);
        }

        if (!jobStore.TryGet(jobId, out var updatedJob))
        {
            updatedJob = job;
        }

        return UpgradeJobViewModelMapper.ToViewModel(updatedJob);
    }

    public async Task<UiUpgradeJob> StartRollbackAsync(string upgradeJobId, string initiatedBy, CancellationToken ct = default)
    {
        if (jobStore.TryGetLatestActive(out var active))
        {
            throw new InvalidOperationException($"Another upgrade or rollback operation is already running. Active job: {active.Id}.");
        }

        var rollbackEntry = rollbackStore.GetLatest();
        if (rollbackEntry is null ||
            !rollbackEntry.CanExecute ||
            !string.Equals(rollbackEntry.UpgradeJobId, upgradeJobId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Rollback is not available for the selected upgrade job.");
        }

        if (!rollbackStore.TryBeginRestore(rollbackEntry.Id, out rollbackEntry, out var message))
        {
            throw new InvalidOperationException(message);
        }

        var user = userContextProvider.GetCurrentUser();
        var productKey = jobStore.TryGet(upgradeJobId, out var originalJob)
            ? originalJob.ProductKey
            : productContext.Current.Key;
        var rollbackDeployments = rollbackEntry.KubernetesResult?.PreviousImages
            .Select(image => image.DeploymentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var rollbackJob = jobStore.CreateRollback(
            rollbackEntry.TargetVersion ?? rollbackEntry.BackupResult.SelectedVersion,
            rollbackEntry.PreviousVersion ?? "Rollback point",
            rollbackEntry.Id,
            rollbackDeployments,
            user,
            productKey);

        await historyStore.SaveStartedAsync(new UpgradeHistoryRecord
        {
            JobId = rollbackJob.Id,
            ParentJobId = rollbackEntry.UpgradeJobId,
            Product = UpgradeProductDefinitions.Resolve(productKey).Key,
            OperationType = UpgradeHistoryOperationTypes.ManualRollback,
            PreviousVersion = rollbackEntry.TargetVersion ?? rollbackEntry.BackupResult.SelectedVersion,
            TargetVersion = rollbackEntry.PreviousVersion ?? "Rollback point",
            UpgradeType = "Rollback",
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow,
            InitiatedByUserId = user.UserId,
            InitiatedByName = user.DisplayName,
            InitiatedByEmail = user.Email,
            InitiatedByRole = user.Role,
            RollbackMode = UpgradeHistoryRollbackModes.Manual,
            RollbackJobId = rollbackJob.Id
        }, ct);

        rollbackJobRunner.Start(rollbackJob.Id, rollbackEntry.Id);
        return UpgradeJobViewModelMapper.ToViewModel(rollbackJob);
    }

    private static UpgradeHistoryRecord CreateHistoryRecord(UpgradeJob job)
    {
        return new UpgradeHistoryRecord
        {
            JobId = job.Id,
            Product = UpgradeProductDefinitions.Resolve(job.ProductKey).Key,
            OperationType = job.UpgradeType == UpgradeJobType.CustomVersionOrPatch
                ? UpgradeHistoryOperationTypes.CustomVersionOrPatchUpgrade
                : UpgradeHistoryOperationTypes.StandardReleaseUpgrade,
            PreviousVersion = job.CurrentVersion,
            TargetVersion = job.TargetVersion,
            UpgradeType = job.UpgradeType.ToString(),
            Status = UpgradeJobStatus.Running.ToString(),
            StartedAt = job.StartedAt,
            InitiatedByUserId = job.InitiatedBy.UserId,
            InitiatedByName = job.InitiatedBy.DisplayName,
            InitiatedByEmail = job.InitiatedBy.Email,
            InitiatedByRole = job.InitiatedBy.Role
        };
    }

    private async Task TryUpdateHistoryStatusAsync(string jobId, string status, CancellationToken cancellationToken)
    {
        try
        {
            await historyStore.UpdateStatusAsync(jobId, status, cancellationToken);
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

    private static string BuildCancellationRequestMessage(UpgradeJob job)
    {
        return job.CurrentStage switch
        {
            UpgradeJobStageName.KubernetesImageUpgrade or UpgradeJobStageName.PostUpgradeValidation =>
                "Upgrade cancellation requested. Deployment images may already have changed, so automatic rollback will be started if rollback context is available.",
            UpgradeJobStageName.AutomaticRollback =>
                "Cancellation request ignored because automatic rollback is already running.",
            _ =>
                "Upgrade cancellation requested. Deployment images have not been changed yet; the current operation will be stopped and temporary resources will be cleaned up where possible."
        };
    }
}
