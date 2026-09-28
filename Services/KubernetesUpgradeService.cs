using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class KubernetesUpgradeService : IKubernetesUpgradeService
{
    private static readonly IReadOnlyList<string> DefaultExpectedDeployments =
    [
        "bi-api-deployment",
        "bi-dataservice-deployment",
        "bi-jobs-deployment",
        "bi-web-deployment",
        "bold-ai-deployment",
        "bold-etl-deployment",
        "id-api-deployment",
        "id-ums-deployment",
        "id-web-deployment"
    ];

    private static readonly IReadOnlyList<string> DefaultOptionalDeployments =
    [
        "bold-ai-deployment",
        "bold-etl-deployment"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpMethod PatchMethod = new("PATCH");

    private readonly HttpClient releaseHttpClient;
    private readonly UpgradeCenterOptions upgradeCenterOptions;
    private readonly KubernetesUpgradeOptions options;
    private readonly IUpgradeProductContext productContext;
    private readonly IHostEnvironment hostEnvironment;
    private readonly ILogger<KubernetesUpgradeService> logger;

    public KubernetesUpgradeService(
        HttpClient releaseHttpClient,
        IOptions<UpgradeCenterOptions> upgradeCenterOptions,
        IOptions<KubernetesUpgradeOptions> options,
        IUpgradeProductContext productContext,
        IHostEnvironment hostEnvironment,
        ILogger<KubernetesUpgradeService> logger)
    {
        this.releaseHttpClient = releaseHttpClient;
        this.upgradeCenterOptions = upgradeCenterOptions.Value;
        this.options = options.Value;
        this.productContext = productContext;
        this.hostEnvironment = hostEnvironment;
        this.logger = logger;
    }

    public async Task<KubernetesNamespaceValidationResult> ValidateNamespaceAsync(CancellationToken cancellationToken = default)
    {
        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return CreateFailedValidation(context.Namespace ?? "Unknown", context.Message);
        }

        try
        {
            using var client = CreateKubernetesHttpClient(context);
            var deployments = await ListDeploymentsAsync(client, context.Namespace!, cancellationToken);
            return BuildValidationResult(context.Namespace!, deployments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to validate Kubernetes namespace.");
            return CreateFailedValidation(context.Namespace ?? "Unknown", $"Unable to validate Kubernetes namespace: {exception.Message}");
        }
    }

    public async Task<KubernetesUpgradeResult> UpgradeAsync(
        string selectedVersion,
        KubernetesUpgradeScope scope,
        string? deploymentName,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null,
        Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared = null)
    {
        if (!options.Enabled)
        {
            return KubernetesUpgradeResult.CreateSkipped("Kubernetes upgrade is disabled by configuration.");
        }

        if (string.IsNullOrWhiteSpace(selectedVersion) || selectedVersion.Equals("Not selected", StringComparison.OrdinalIgnoreCase))
        {
            return KubernetesUpgradeResult.Failed("Kubernetes upgrade skipped because no upgrade version was selected.", scope, deploymentName);
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return KubernetesUpgradeResult.Failed(context.Message, scope, deploymentName);
        }

        var imageMap = await FetchReleaseImagesAsync(selectedVersion, cancellationToken);
        if (imageMap.Count == 0)
        {
            return KubernetesUpgradeResult.Failed("Kubernetes image API did not return deployment image details.", scope, deploymentName);
        }

        using var client = CreateKubernetesHttpClient(context);
        var deployments = await ListDeploymentsAsync(client, context.Namespace!, cancellationToken);
        var validation = BuildValidationResult(context.Namespace!, deployments);
        if (!validation.Succeeded)
        {
            return KubernetesUpgradeResult.Failed(validation.Message, scope, deploymentName, validation);
        }

        var targetDeployments = ResolveTargetDeployments(scope, deploymentName, validation, imageMap);
        if (targetDeployments.Count == 0)
        {
            return KubernetesUpgradeResult.Failed($"No matching {productContext.Current.DisplayName} Kubernetes deployments were available for upgrade.", scope, deploymentName, validation);
        }

        var missingRequiredImages = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsRequired && deployment.IsAvailable)
            .Where(deployment => !imageMap.ContainsKey(deployment.DeploymentName))
            .Select(deployment => deployment.DeploymentName)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingRequiredImages.Count > 0)
        {
            return KubernetesUpgradeResult.Failed(
                $"{productContext.Current.DisplayName} release metadata is missing image mapping for required deployment(s): {string.Join(", ", missingRequiredImages)}.",
                scope,
                deploymentName,
                validation);
        }

        return await UpgradeResolvedDeploymentsAsync(
            client,
            context.Namespace!,
            targetDeployments,
            imageMap,
            validation,
            scope,
            deploymentName,
            cancellationToken,
            progress,
            rollbackContextPrepared);
    }

    public async Task<KubernetesUpgradeResult> UpgradeWithCustomImagesAsync(
        string selectedVersion,
        IReadOnlyList<CustomPatchImageMapping> images,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null,
        Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared = null)
    {
        if (!options.Enabled)
        {
            return KubernetesUpgradeResult.CreateSkipped("Kubernetes upgrade is disabled by configuration.");
        }

        if (string.IsNullOrWhiteSpace(selectedVersion) || selectedVersion.Equals("Not selected", StringComparison.OrdinalIgnoreCase))
        {
            return KubernetesUpgradeResult.Failed("Kubernetes custom patch skipped because no target version was selected.", KubernetesUpgradeScope.Bulk);
        }

        var imageMap = images
            .Where(image => !string.IsNullOrWhiteSpace(image.DeploymentName) && !string.IsNullOrWhiteSpace(image.Image))
            .GroupBy(image => image.DeploymentName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var image = group.First();
                    return new KubernetesDeploymentImageInfo(image.DeploymentName.Trim(), image.Image.Trim(), image.ContainerName);
                },
                StringComparer.OrdinalIgnoreCase);

        if (imageMap.Count == 0)
        {
            return KubernetesUpgradeResult.Failed("Kubernetes custom patch skipped because no custom deployment images were supplied.", KubernetesUpgradeScope.Bulk);
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return KubernetesUpgradeResult.Failed(context.Message, KubernetesUpgradeScope.Bulk);
        }

        using var client = CreateKubernetesHttpClient(context);
        var deployments = await ListDeploymentsAsync(client, context.Namespace!, cancellationToken);
        var validation = BuildValidationResult(context.Namespace!, deployments);
        if (!validation.Succeeded)
        {
            return KubernetesUpgradeResult.Failed(validation.Message, KubernetesUpgradeScope.Bulk, validation: validation);
        }

        var targetDeployments = validation.MatchedDeployments
            .Where(imageMap.ContainsKey)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targetDeployments.Count == 0)
        {
            return KubernetesUpgradeResult.Failed($"No matching {productContext.Current.DisplayName} Kubernetes deployments were available for custom patch upgrade.", KubernetesUpgradeScope.Bulk, validation: validation);
        }

        var missingRequiredImages = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsRequired && deployment.IsAvailable)
            .Where(deployment => !imageMap.ContainsKey(deployment.DeploymentName))
            .Select(deployment => deployment.DeploymentName)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingRequiredImages.Count > 0)
        {
            return KubernetesUpgradeResult.Failed($"Custom patch image mapping is missing for required deployment(s): {string.Join(", ", missingRequiredImages)}.", KubernetesUpgradeScope.Bulk, validation: validation);
        }

        return await UpgradeResolvedDeploymentsAsync(client, context.Namespace!, targetDeployments, imageMap, validation, KubernetesUpgradeScope.Bulk, null, cancellationToken, progress, rollbackContextPrepared);
    }

    public async Task<KubernetesRollbackResult> RollbackAsync(
        KubernetesUpgradeResult upgradeResult,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null)
    {
        if (upgradeResult.PreviousImages.Count == 0)
        {
            return KubernetesRollbackResult.CreateSkipped("Kubernetes rollback skipped because no previous deployment image mapping is available.");
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return new KubernetesRollbackResult(false, false, context.Message, context.Namespace, Array.Empty<KubernetesDeploymentRollbackResult>());
        }

        using var client = CreateKubernetesHttpClient(context);
        var results = await Task.WhenAll(upgradeResult.PreviousImages.Select(snapshot =>
            RollbackDeploymentAsync(client, context.Namespace!, snapshot, cancellationToken, progress)));

        var succeeded = results.Length == upgradeResult.PreviousImages.Count && results.All(result => result.Succeeded);
        var message = succeeded
            ? "Kubernetes deployment images were rolled back successfully."
            : "Kubernetes deployment image rollback failed for one or more deployments.";

        return new KubernetesRollbackResult(succeeded, false, message, context.Namespace, results);
    }

    public async Task<KubernetesRollbackResult> ReconcileRollbackAsync(
        KubernetesUpgradeResult upgradeResult,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null)
    {
        if (upgradeResult.PreviousImages.Count == 0)
        {
            return KubernetesRollbackResult.CreateSkipped("Kubernetes rollback reconciliation skipped because no previous deployment image mapping is available.");
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return new KubernetesRollbackResult(false, false, context.Message, context.Namespace, Array.Empty<KubernetesDeploymentRollbackResult>());
        }

        using var client = CreateKubernetesHttpClient(context);
        var results = await Task.WhenAll(upgradeResult.PreviousImages.Select(snapshot =>
            ReconcileRollbackDeploymentAsync(client, context.Namespace!, snapshot, cancellationToken, progress)));

        var succeeded = results.Length == upgradeResult.PreviousImages.Count && results.All(result => result.Succeeded);
        var message = succeeded
            ? "Kubernetes deployment rollback rollouts were reconciled successfully."
            : "Kubernetes deployment rollback reconciliation detected one or more unhealthy deployments.";

        return new KubernetesRollbackResult(succeeded, false, message, context.Namespace, results);
    }

    public async Task<KubernetesUpgradeResult> ReconcileUpgradeRolloutAsync(
        KubernetesUpgradeResult upgradeResult,
        CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null)
    {
        if (upgradeResult.Skipped)
        {
            return upgradeResult;
        }

        if (upgradeResult.DeploymentResults.Count == 0)
        {
            return upgradeResult with
            {
                Succeeded = false,
                Message = "Kubernetes rollout recovery failed because no deployment rollout context was persisted."
            };
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return upgradeResult with
            {
                Succeeded = false,
                Message = context.Message,
                Namespace = context.Namespace
            };
        }

        using var client = CreateKubernetesHttpClient(context);
        var results = await Task.WhenAll(upgradeResult.DeploymentResults.Select(result =>
            ReconcileDeploymentRolloutAsync(client, context.Namespace!, result, cancellationToken, progress)));
        var succeeded = results.Length > 0 && results.All(result => result.Succeeded);
        var message = succeeded
            ? "Kubernetes deployment rollouts recovered successfully after Upgrade Center restart."
            : "Kubernetes deployment rollout recovery detected one or more failed deployments.";

        return upgradeResult with
        {
            Succeeded = succeeded,
            Message = message,
            Namespace = context.Namespace,
            DeploymentResults = results
        };
    }

    public async Task<IReadOnlyDictionary<string, KubernetesDeploymentImageInfo>> GetReleaseImagesAsync(
        string selectedVersion,
        CancellationToken cancellationToken = default)
    {
        return await FetchReleaseImagesAsync(selectedVersion, cancellationToken);
    }

    private async Task<Dictionary<string, KubernetesDeploymentImageInfo>> FetchReleaseImagesAsync(string selectedVersion, CancellationToken cancellationToken)
    {
        var uri = BuildVersionApiUri(selectedVersion, "k8s");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Bold-Upgrade-Center/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        logger.LogInformation("Fetching Kubernetes image details for version {SelectedVersion} from {Uri}.", selectedVersion, uri);
        using var response = await releaseHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var imageResponse = await JsonSerializer.DeserializeAsync<KubernetesReleaseImageResponse>(stream, JsonOptions, cancellationToken);
        return (imageResponse?.Deployments ?? [])
            .Where(deployment => !string.IsNullOrWhiteSpace(deployment.Name) && !string.IsNullOrWhiteSpace(deployment.ImageTag))
            .GroupBy(deployment => deployment.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var deployment = group.First();
                    return new KubernetesDeploymentImageInfo(deployment.Name!, deployment.ImageTag!, deployment.ContainerName);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<KubernetesDeploymentResource>> ListDeploymentsAsync(HttpClient client, string kubernetesNamespace, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/apis/apps/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/deployments", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var deploymentList = await JsonSerializer.DeserializeAsync<KubernetesDeploymentList>(stream, JsonOptions, cancellationToken);
        return deploymentList?.Items ?? new List<KubernetesDeploymentResource>();
    }

    private async Task<KubernetesDeploymentResource> GetDeploymentAsync(HttpClient client, string kubernetesNamespace, string deploymentName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/apis/apps/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/deployments/{Uri.EscapeDataString(deploymentName)}", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<KubernetesDeploymentResource>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Kubernetes deployment '{deploymentName}' returned an empty response.");
    }

    private async Task PatchDeploymentImageAsync(
        HttpClient client,
        string kubernetesNamespace,
        string deploymentName,
        string containerName,
        string image,
        CancellationToken cancellationToken)
    {
        var patch = new
        {
            spec = new
            {
                template = new
                {
                    spec = new
                    {
                        containers = new[]
                        {
                            new
                            {
                                name = containerName,
                                image
                            }
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(
            PatchMethod,
            $"/apis/apps/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/deployments/{Uri.EscapeDataString(deploymentName)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(patch, JsonOptions), Encoding.UTF8, "application/strategic-merge-patch+json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<KubernetesRolloutHealthResult> WaitForDeploymentRolloutAsync(
        HttpClient client,
        string kubernetesNamespace,
        string deploymentName,
        string containerName,
        string expectedImage,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(30, options.RolloutTimeoutSeconds));
        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(options.RolloutPollSeconds, 1, 30));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? lastMessage = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deployment = await GetDeploymentAsync(client, kubernetesNamespace, deploymentName, cancellationToken);
            var health = EvaluateRolloutHealth(deployment, containerName, expectedImage);
            if (health.Succeeded)
            {
                return health;
            }

            lastMessage = health.Message;
            if (health.IsTerminal)
            {
                return health;
            }

            await Task.Delay(pollDelay, cancellationToken);
        }

        return KubernetesRolloutHealthResult.Failed(
            $"Deployment rollout did not become healthy within {timeout.TotalSeconds:0} seconds. Last status: {lastMessage ?? "status was unavailable"}");
    }

    private static KubernetesRolloutHealthResult EvaluateRolloutHealth(
        KubernetesDeploymentResource deployment,
        string containerName,
        string expectedImage)
    {
        var deploymentName = deployment.Metadata?.Name ?? "Unknown";
        var generation = deployment.Metadata?.Generation ?? 0;
        var observedGeneration = deployment.Status?.ObservedGeneration ?? 0;
        if (generation > 0 && observedGeneration < generation)
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' controller has not observed generation {generation}. Observed generation: {observedGeneration}.");
        }

        var progressing = FindCondition(deployment, "Progressing");
        if (string.Equals(progressing?.Reason, "ProgressDeadlineExceeded", StringComparison.OrdinalIgnoreCase))
        {
            return KubernetesRolloutHealthResult.Failed(
                $"Deployment '{deploymentName}' rollout exceeded its progress deadline. {progressing?.Message}");
        }

        var container = deployment.Spec?.Template?.Spec?.Containers?
            .FirstOrDefault(item => string.Equals(item.Name, containerName, StringComparison.OrdinalIgnoreCase));
        if (container is null)
        {
            return KubernetesRolloutHealthResult.Failed(
                $"Deployment '{deploymentName}' no longer contains container '{containerName}'.");
        }

        if (!string.Equals(container.Image, expectedImage, StringComparison.Ordinal))
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' template has not been updated to the expected image.");
        }

        var desiredReplicas = deployment.Spec?.Replicas ?? 1;
        var updatedReplicas = deployment.Status?.UpdatedReplicas ?? 0;
        var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;
        var availableReplicas = deployment.Status?.AvailableReplicas ?? 0;
        var unavailableReplicas = deployment.Status?.UnavailableReplicas ?? 0;

        if (updatedReplicas < desiredReplicas)
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' has updated {updatedReplicas}/{desiredReplicas} replica(s).");
        }

        if (readyReplicas < desiredReplicas)
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' has ready {readyReplicas}/{desiredReplicas} replica(s).");
        }

        if (availableReplicas < desiredReplicas)
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' has available {availableReplicas}/{desiredReplicas} replica(s).");
        }

        if (unavailableReplicas > 0)
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' still reports {unavailableReplicas} unavailable replica(s).");
        }

        var available = FindCondition(deployment, "Available");
        if (available is not null && !string.Equals(available.Status, "True", StringComparison.OrdinalIgnoreCase))
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' is not available yet. {available.Message}");
        }

        if (progressing is not null && !string.Equals(progressing.Status, "True", StringComparison.OrdinalIgnoreCase))
        {
            return KubernetesRolloutHealthResult.Waiting(
                $"Deployment '{deploymentName}' is not progressing successfully. {progressing.Message}");
        }

        return KubernetesRolloutHealthResult.Healthy(
            $"Deployment image updated and rollout is healthy. Ready replicas: {readyReplicas}/{desiredReplicas}.");
    }

    private static KubernetesDeploymentCondition? FindCondition(KubernetesDeploymentResource deployment, string type)
    {
        return deployment.Status?.Conditions?
            .FirstOrDefault(condition => string.Equals(condition.Type, type, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<KubernetesUpgradeResult> UpgradeResolvedDeploymentsAsync(
        HttpClient client,
        string kubernetesNamespace,
        IReadOnlyList<string> targetDeployments,
        Dictionary<string, KubernetesDeploymentImageInfo> imageMap,
        KubernetesNamespaceValidationResult validation,
        KubernetesUpgradeScope scope,
        string? deploymentName,
        CancellationToken cancellationToken,
        Action<KubernetesDeploymentProgress>? progress,
        Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared)
    {
        var deploymentResults = new List<KubernetesDeploymentUpgradeResult>();
        var modifiedImages = new ConcurrentBag<KubernetesDeploymentImageSnapshot>();
        var preparationTasks = targetDeployments.Select(targetDeployment =>
            PrepareDeploymentUpgradeAsync(client, kubernetesNamespace, targetDeployment, imageMap, cancellationToken, progress));
        var prepared = await Task.WhenAll(preparationTasks);
        var preparationFailures = prepared
            .Where(item => item.Result is not null)
            .Select(item => item.Result!)
            .ToList();
        if (preparationFailures.Count > 0)
        {
            deploymentResults.AddRange(preparationFailures);
            return CreateKubernetesUpgradeResult(false, kubernetesNamespace, scope, deploymentName, deploymentResults, modifiedImages, validation);
        }

        var workItems = prepared
            .Select(item => item.WorkItem)
            .WhereNotNull()
            .ToList();
        if (workItems.Count == 0)
        {
            deploymentResults.Add(new KubernetesDeploymentUpgradeResult(
                false,
                "Unknown",
                null,
                null,
                null,
                "No deployment images were prepared for upgrade."));
            return CreateKubernetesUpgradeResult(false, kubernetesNamespace, scope, deploymentName, deploymentResults, modifiedImages, validation);
        }

        if (rollbackContextPrepared is not null)
        {
            var preparedRollbackContext = CreateKubernetesUpgradeResult(
                false,
                kubernetesNamespace,
                scope,
                deploymentName,
                workItems
                    .Select(item => new KubernetesDeploymentUpgradeResult(
                        false,
                        item.DeploymentName,
                        item.ContainerName,
                        item.PreviousImage,
                        item.TargetImage,
                        "Deployment image prepared; rollout pending."))
                    .ToList(),
                workItems.Select(item => new KubernetesDeploymentImageSnapshot(item.DeploymentName, item.ContainerName, item.PreviousImage)),
                validation);
            await rollbackContextPrepared(preparedRollbackContext);
        }

        foreach (var group in ResolveDeploymentGroups(workItems))
        {
            var groupResults = await Task.WhenAll(group.Select(item =>
                UpgradeDeploymentAsync(client, kubernetesNamespace, item, modifiedImages, cancellationToken, progress)));
            deploymentResults.AddRange(groupResults);
            if (groupResults.Any(result => !result.Succeeded))
            {
                break;
            }
        }

        var succeeded = deploymentResults.Count > 0 && deploymentResults.All(result => result.Succeeded);
        return CreateKubernetesUpgradeResult(succeeded, kubernetesNamespace, scope, deploymentName, deploymentResults, modifiedImages, validation);
    }

    private async Task<KubernetesPreparedDeployment> PrepareDeploymentUpgradeAsync(
        HttpClient client,
        string kubernetesNamespace,
        string targetDeployment,
        Dictionary<string, KubernetesDeploymentImageInfo> imageMap,
        CancellationToken cancellationToken,
        Action<KubernetesDeploymentProgress>? progress)
    {
        try
        {
            var releaseImage = imageMap[targetDeployment];
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                targetDeployment,
                KubernetesDeploymentProgressStatus.RecordingPreviousImage,
                "Recording current deployment image before upgrade."));

            var deployment = await GetDeploymentAsync(client, kubernetesNamespace, targetDeployment, cancellationToken);
            var container = SelectContainer(deployment, releaseImage);
            if (container is null || string.IsNullOrWhiteSpace(container.Name) || string.IsNullOrWhiteSpace(container.Image))
            {
                return KubernetesPreparedDeployment.Failed(new KubernetesDeploymentUpgradeResult(
                    false,
                    targetDeployment,
                    releaseImage.ContainerName,
                    null,
                    releaseImage.Image,
                    "Deployment does not contain a container that can be updated."));
            }

            return KubernetesPreparedDeployment.Prepared(new KubernetesDeploymentUpgradeWorkItem(
                targetDeployment,
                container.Name,
                container.Image,
                releaseImage.Image));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to prepare Kubernetes image update for deployment {Deployment}.", targetDeployment);
            return KubernetesPreparedDeployment.Failed(new KubernetesDeploymentUpgradeResult(
                false,
                targetDeployment,
                null,
                null,
                imageMap.TryGetValue(targetDeployment, out var releaseImage) ? releaseImage.Image : null,
                $"Deployment image preparation failed: {exception.Message}"));
        }
    }

    private async Task<KubernetesDeploymentUpgradeResult> UpgradeDeploymentAsync(
        HttpClient client,
        string kubernetesNamespace,
        KubernetesDeploymentUpgradeWorkItem item,
        ConcurrentBag<KubernetesDeploymentImageSnapshot> modifiedImages,
        CancellationToken cancellationToken,
        Action<KubernetesDeploymentProgress>? progress)
    {
        try
        {
            modifiedImages.Add(new KubernetesDeploymentImageSnapshot(item.DeploymentName, item.ContainerName, item.PreviousImage));
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                item.DeploymentName,
                KubernetesDeploymentProgressStatus.Patching,
                "Patching deployment image."));

            await PatchDeploymentImageAsync(client, kubernetesNamespace, item.DeploymentName, item.ContainerName, item.TargetImage, cancellationToken);
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                item.DeploymentName,
                KubernetesDeploymentProgressStatus.WaitingForRollout,
                "Waiting for deployment rollout to become healthy."));

            var rollout = await WaitForDeploymentRolloutAsync(
                client,
                kubernetesNamespace,
                item.DeploymentName,
                item.ContainerName,
                item.TargetImage,
                cancellationToken);

            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                item.DeploymentName,
                rollout.Succeeded ? KubernetesDeploymentProgressStatus.Completed : KubernetesDeploymentProgressStatus.Failed,
                rollout.Message));

            if (rollout.Succeeded)
            {
                logger.LogInformation(
                    "Updated Kubernetes deployment image and rollout became healthy. Namespace: {Namespace}. Deployment: {Deployment}. Container: {Container}.",
                    kubernetesNamespace,
                    item.DeploymentName,
                    item.ContainerName);
            }
            else
            {
                logger.LogError(
                    "Kubernetes deployment rollout did not become healthy. Namespace: {Namespace}. Deployment: {Deployment}. Container: {Container}. Message: {Message}",
                    kubernetesNamespace,
                    item.DeploymentName,
                    item.ContainerName,
                    rollout.Message);
            }

            return new KubernetesDeploymentUpgradeResult(
                rollout.Succeeded,
                item.DeploymentName,
                item.ContainerName,
                item.PreviousImage,
                item.TargetImage,
                rollout.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Kubernetes image update failed for deployment {Deployment}.", item.DeploymentName);
            var message = $"Deployment image update failed: {exception.Message}";
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                item.DeploymentName,
                KubernetesDeploymentProgressStatus.Failed,
                message));
            return new KubernetesDeploymentUpgradeResult(
                false,
                item.DeploymentName,
                item.ContainerName,
                item.PreviousImage,
                item.TargetImage,
                message);
        }
    }

    private async Task<KubernetesDeploymentRollbackResult> RollbackDeploymentAsync(
        HttpClient client,
        string kubernetesNamespace,
        KubernetesDeploymentImageSnapshot snapshot,
        CancellationToken cancellationToken,
        Action<KubernetesDeploymentProgress>? progress)
    {
        try
        {
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                KubernetesDeploymentProgressStatus.RollingBack,
                "Restoring previous deployment image."));

            await GetDeploymentAsync(client, kubernetesNamespace, snapshot.DeploymentName, cancellationToken);
            await PatchDeploymentImageAsync(client, kubernetesNamespace, snapshot.DeploymentName, snapshot.ContainerName, snapshot.Image, cancellationToken);
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                KubernetesDeploymentProgressStatus.RollbackWaitingForRollout,
                "Deployment image reverted; waiting for rollout health validation."));

            var rollout = await WaitForDeploymentRolloutAsync(
                client,
                kubernetesNamespace,
                snapshot.DeploymentName,
                snapshot.ContainerName,
                snapshot.Image,
                cancellationToken);

            var message = rollout.Succeeded
                ? "Deployment image restored and rollout became healthy."
                : $"Deployment image was restored, but rollout did not become healthy: {rollout.Message}";
            var timedOut = IsRolloutTimeout(rollout.Message);
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                rollout.Succeeded
                    ? KubernetesDeploymentProgressStatus.RollbackCompleted
                    : timedOut
                        ? KubernetesDeploymentProgressStatus.RollbackWaitingForRollout
                        : KubernetesDeploymentProgressStatus.RollbackFailed,
                timedOut ? $"{message} Rechecking actual Kubernetes rollout state." : message));

            if (rollout.Succeeded)
            {
                logger.LogWarning(
                    "Rolled back Kubernetes deployment image and rollout became healthy. Namespace: {Namespace}. Deployment: {Deployment}. Container: {Container}.",
                    kubernetesNamespace,
                    snapshot.DeploymentName,
                    snapshot.ContainerName);
            }
            else
            {
                logger.LogError(
                    "Kubernetes rollback image patch completed, but rollout did not become healthy. Namespace: {Namespace}. Deployment: {Deployment}. Container: {Container}. Message: {Message}",
                    kubernetesNamespace,
                    snapshot.DeploymentName,
                    snapshot.ContainerName,
                    rollout.Message);
            }

            return new KubernetesDeploymentRollbackResult(
                rollout.Succeeded,
                snapshot.DeploymentName,
                snapshot.ContainerName,
                snapshot.Image,
                message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Kubernetes image rollback failed for deployment {Deployment}.", snapshot.DeploymentName);
            var message = $"Deployment image rollback failed: {exception.Message}";
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                KubernetesDeploymentProgressStatus.RollbackFailed,
                message));
            return new KubernetesDeploymentRollbackResult(
                false,
                snapshot.DeploymentName,
                snapshot.ContainerName,
                snapshot.Image,
                message);
        }
    }

    private async Task<KubernetesDeploymentRollbackResult> ReconcileRollbackDeploymentAsync(
        HttpClient client,
        string kubernetesNamespace,
        KubernetesDeploymentImageSnapshot snapshot,
        CancellationToken cancellationToken,
        Action<KubernetesDeploymentProgress>? progress)
    {
        try
        {
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                KubernetesDeploymentProgressStatus.RollbackWaitingForRollout,
                "Rechecking reverted deployment rollout status."));

            await GetDeploymentAsync(client, kubernetesNamespace, snapshot.DeploymentName, cancellationToken);
            var rollout = await WaitForDeploymentRolloutAsync(
                client,
                kubernetesNamespace,
                snapshot.DeploymentName,
                snapshot.ContainerName,
                snapshot.Image,
                cancellationToken);

            var message = rollout.Succeeded
                ? "Deployment image restore was verified and rollout is healthy."
                : $"Deployment image restore was rechecked, but rollout is not healthy: {rollout.Message}";
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                rollout.Succeeded ? KubernetesDeploymentProgressStatus.RollbackCompleted : KubernetesDeploymentProgressStatus.RollbackFailed,
                message));

            return new KubernetesDeploymentRollbackResult(
                rollout.Succeeded,
                snapshot.DeploymentName,
                snapshot.ContainerName,
                snapshot.Image,
                message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Kubernetes rollback reconciliation failed for deployment {Deployment}.", snapshot.DeploymentName);
            var message = $"Deployment rollback reconciliation failed: {exception.Message}";
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Rollback,
                snapshot.DeploymentName,
                KubernetesDeploymentProgressStatus.RollbackFailed,
                message));
            return new KubernetesDeploymentRollbackResult(
                false,
                snapshot.DeploymentName,
                snapshot.ContainerName,
                snapshot.Image,
                message);
        }
    }

    private static bool IsRolloutTimeout(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               message.Contains("did not become healthy within", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<KubernetesDeploymentUpgradeResult> ReconcileDeploymentRolloutAsync(
        HttpClient client,
        string kubernetesNamespace,
        KubernetesDeploymentUpgradeResult persistedResult,
        CancellationToken cancellationToken,
        Action<KubernetesDeploymentProgress>? progress)
    {
        if (persistedResult.Succeeded)
        {
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                persistedResult.DeploymentName,
                KubernetesDeploymentProgressStatus.Completed,
                persistedResult.Message));
            return persistedResult;
        }

        if (string.IsNullOrWhiteSpace(persistedResult.ContainerName) ||
            string.IsNullOrWhiteSpace(persistedResult.NewImage))
        {
            var missingContextMessage = "Deployment rollout recovery failed because the container name or target image was not persisted.";
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                persistedResult.DeploymentName,
                KubernetesDeploymentProgressStatus.Failed,
                missingContextMessage));
            return persistedResult with
            {
                Succeeded = false,
                Message = missingContextMessage
            };
        }

        try
        {
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                persistedResult.DeploymentName,
                KubernetesDeploymentProgressStatus.WaitingForRollout,
                "Recovering deployment rollout status after Upgrade Center restart."));

            var rollout = await WaitForDeploymentRolloutAsync(
                client,
                kubernetesNamespace,
                persistedResult.DeploymentName,
                persistedResult.ContainerName,
                persistedResult.NewImage,
                cancellationToken);

            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                persistedResult.DeploymentName,
                rollout.Succeeded ? KubernetesDeploymentProgressStatus.Completed : KubernetesDeploymentProgressStatus.Failed,
                rollout.Message));

            return persistedResult with
            {
                Succeeded = rollout.Succeeded,
                Message = rollout.Message
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Kubernetes rollout recovery failed for deployment {Deployment}.", persistedResult.DeploymentName);
            var message = $"Deployment rollout recovery failed: {exception.Message}";
            progress?.Invoke(new KubernetesDeploymentProgress(
                KubernetesDeploymentProgressKind.Upgrade,
                persistedResult.DeploymentName,
                KubernetesDeploymentProgressStatus.Failed,
                message));
            return persistedResult with
            {
                Succeeded = false,
                Message = message
            };
        }
    }

    private KubernetesUpgradeResult CreateKubernetesUpgradeResult(
        bool succeeded,
        string kubernetesNamespace,
        KubernetesUpgradeScope scope,
        string? deploymentName,
        IReadOnlyList<KubernetesDeploymentUpgradeResult> deploymentResults,
        IEnumerable<KubernetesDeploymentImageSnapshot> previousImages,
        KubernetesNamespaceValidationResult validation)
    {
        var message = succeeded
            ? "Kubernetes deployment images were updated successfully."
            : "Kubernetes deployment image update failed for one or more deployments.";

        return new KubernetesUpgradeResult(
            succeeded,
            false,
            message,
            kubernetesNamespace,
            scope,
            deploymentName,
            deploymentResults,
            previousImages
                .GroupBy(snapshot => $"{snapshot.DeploymentName}|{snapshot.ContainerName}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(snapshot => snapshot.DeploymentName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            validation);
    }

    private KubernetesRuntimeContext ResolveContext()
    {
        var kubernetesNamespace = FirstConfiguredValue(
            options.Namespace,
            Environment.GetEnvironmentVariable("POD_NAMESPACE"),
            ReadFileIfExists(options.NamespacePath));

        var apiServer = FirstConfiguredValue(
            options.ApiServer,
            ResolveInClusterApiServer());

        if (string.IsNullOrWhiteSpace(kubernetesNamespace))
        {
            return KubernetesRuntimeContext.Failed(null, "Kubernetes namespace could not be resolved from configuration, POD_NAMESPACE, or the service account namespace file.");
        }

        if (string.IsNullOrWhiteSpace(apiServer))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, "Kubernetes API server could not be resolved from configuration or in-cluster environment variables.");
        }

        if (!File.Exists(options.ServiceAccountTokenPath))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, $"Kubernetes service account token was not found at '{options.ServiceAccountTokenPath}'.");
        }

        var token = File.ReadAllText(options.ServiceAccountTokenPath).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, "Kubernetes service account token file is empty.");
        }

        return new KubernetesRuntimeContext(true, kubernetesNamespace.Trim(), apiServer.TrimEnd('/'), token, "Kubernetes runtime context resolved.");
    }

    private HttpClient CreateKubernetesHttpClient(KubernetesRuntimeContext context)
    {
        var handler = new HttpClientHandler();
        if (options.SkipTlsVerify)
        {
            if (!CanSkipTlsVerify())
            {
                logger.LogError("Kubernetes TLS verification bypass was blocked because SkipTlsVerify is enabled without an explicit local development override.");
                throw new InvalidOperationException("Kubernetes TLS verification cannot be disabled unless an explicit local development override is enabled. Configure the service account certificate authority file instead.");
            }

            logger.LogWarning("Kubernetes TLS verification is disabled by explicit local development configuration.");
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (File.Exists(options.CertificateAuthorityPath))
        {
            var certificateAuthority = new X509Certificate2(options.CertificateAuthorityPath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                if (sslPolicyErrors == SslPolicyErrors.None)
                {
                    return true;
                }

                using var customChain = new X509Chain();
                customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                customChain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);
                customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return customChain.Build(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
            };
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(context.ApiServer!)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private bool CanSkipTlsVerify()
    {
        return options.AllowSkipTlsVerifyForLocalDevelopment &&
            IsLocalDevelopmentEnvironment(hostEnvironment.EnvironmentName);
    }

    private static bool IsLocalDevelopmentEnvironment(string? environmentName)
    {
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Dev", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "LocalDevelopment", StringComparison.OrdinalIgnoreCase);
    }

    private KubernetesNamespaceValidationResult BuildValidationResult(string kubernetesNamespace, IReadOnlyList<KubernetesDeploymentResource> deployments)
    {
        var expected = ResolveExpectedDeployments();
        var optional = ResolveOptionalDeployments(expected);
        var optionalSet = optional.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = expected
            .Where(expectedDeployment => !optionalSet.Contains(expectedDeployment))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deploymentByName = deployments
            .Where(deployment => !string.IsNullOrWhiteSpace(deployment.Metadata?.Name))
            .GroupBy(deployment => deployment.Metadata!.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var available = deploymentByName.Keys
            .Where(deployment => !string.IsNullOrWhiteSpace(deployment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matched = expected
            .Where(expectedDeployment => available.Contains(expectedDeployment, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = expected
            .Where(expectedDeployment => !available.Contains(expectedDeployment, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missingRequired = required
            .Where(requiredDeployment => !available.Contains(requiredDeployment, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingOptional = optional
            .Where(optionalDeployment => expected.Contains(optionalDeployment, StringComparer.OrdinalIgnoreCase) &&
                                         !available.Contains(optionalDeployment, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var expectedStatuses = expected
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .Select(deploymentName =>
            {
                deploymentByName.TryGetValue(deploymentName, out var deployment);
                var isAvailable = deployment is not null;
                var isRequired = required.Contains(deploymentName, StringComparer.OrdinalIgnoreCase);
                var runtimeStatus = "Missing";
                var runtimeStatusDetail = "Deployment was not found in the current namespace.";
                if (isAvailable)
                {
                    runtimeStatus = ResolveDeploymentRuntimeStatus(deployment!, out runtimeStatusDetail);
                }

                return new KubernetesDeploymentDiscoveryItem(
                    deploymentName,
                    true,
                    isAvailable,
                    isRequired,
                    runtimeStatus,
                    runtimeStatusDetail,
                    isAvailable ? GetContainerInfo(deployment!) : Array.Empty<KubernetesDeploymentContainerInfo>());
            })
            .ToList();
        var unsupportedStatuses = available
            .Where(deploymentName => !expected.Contains(deploymentName, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .Select(deploymentName =>
            {
                deploymentByName.TryGetValue(deploymentName, out var deployment);
                var runtimeStatus = "Unknown";
                var runtimeStatusDetail = "Deployment status was not available.";
                if (deployment is not null)
                {
                    runtimeStatus = ResolveDeploymentRuntimeStatus(deployment, out runtimeStatusDetail);
                }

                return new KubernetesDeploymentDiscoveryItem(
                    deploymentName,
                    false,
                    true,
                    false,
                    runtimeStatus,
                    runtimeStatusDetail,
                    deployment is not null ? GetContainerInfo(deployment) : Array.Empty<KubernetesDeploymentContainerInfo>());
            });
        var deploymentStatuses = expectedStatuses
            .Concat(unsupportedStatuses)
            .ToList();

        var succeeded = required.Count > 0 && missingRequired.Count == 0;
        var productName = productContext.Current.DisplayName;
        var message = succeeded
            ? $"Namespace '{kubernetesNamespace}' contains all required {productName} deployments. Optional missing deployment count: {missingOptional.Count}."
            : missingRequired.Count > 0
                ? $"Namespace '{kubernetesNamespace}' is missing required {productName} deployment(s): {string.Join(", ", missingRequired)}."
                : $"Namespace '{kubernetesNamespace}' does not contain the required {productName} deployment list.";

        return new KubernetesNamespaceValidationResult(
            succeeded,
            kubernetesNamespace,
            message,
            expected,
            available,
            matched,
            missing,
            required,
            optional,
            missingRequired,
            missingOptional,
            deploymentStatuses);
    }

    private KubernetesNamespaceValidationResult CreateFailedValidation(string kubernetesNamespace, string message)
    {
        var expected = ResolveExpectedDeployments();
        var optional = ResolveOptionalDeployments(expected);
        var required = expected
            .Where(deployment => !optional.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingOptional = optional
            .Where(deployment => expected.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new KubernetesNamespaceValidationResult(
            false,
            kubernetesNamespace,
            message,
            expected,
            Array.Empty<string>(),
            Array.Empty<string>(),
            expected,
            required,
            optional,
            required,
            missingOptional,
            expected
                .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
                .Select(deployment => new KubernetesDeploymentDiscoveryItem(
                    deployment,
                    true,
                    false,
                    required.Contains(deployment, StringComparer.OrdinalIgnoreCase),
                    "Missing",
                    "Deployment could not be read because namespace validation failed.",
                    Array.Empty<KubernetesDeploymentContainerInfo>()))
                .ToList());
    }

    private static string ResolveDeploymentRuntimeStatus(KubernetesDeploymentResource deployment, out string? detail)
    {
        var deploymentName = deployment.Metadata?.Name ?? "Unknown";
        var desiredReplicas = deployment.Spec?.Replicas ?? 1;
        var generation = deployment.Metadata?.Generation ?? 0;
        var observedGeneration = deployment.Status?.ObservedGeneration ?? 0;
        var updatedReplicas = deployment.Status?.UpdatedReplicas ?? 0;
        var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;
        var availableReplicas = deployment.Status?.AvailableReplicas ?? 0;
        var unavailableReplicas = deployment.Status?.UnavailableReplicas ?? 0;
        var progressing = FindCondition(deployment, "Progressing");
        var available = FindCondition(deployment, "Available");

        if (string.Equals(progressing?.Reason, "ProgressDeadlineExceeded", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"Rollout exceeded the progress deadline. {progressing?.Message}".Trim();
            return "Failed";
        }

        if (generation > 0 && observedGeneration < generation)
        {
            detail = $"Controller has not observed generation {generation}. Observed generation: {observedGeneration}.";
            return "Updating";
        }

        if (desiredReplicas == 0)
        {
            detail = "Deployment is scaled to zero replicas.";
            return "Ready";
        }

        if (updatedReplicas < desiredReplicas)
        {
            detail = $"Updated replicas: {updatedReplicas}/{desiredReplicas}.";
            return "Updating";
        }

        if (unavailableReplicas > 0)
        {
            detail = $"Unavailable replicas: {unavailableReplicas}. Ready replicas: {readyReplicas}/{desiredReplicas}.";
            return "Unavailable";
        }

        if (readyReplicas < desiredReplicas || availableReplicas < desiredReplicas)
        {
            detail = $"Ready replicas: {readyReplicas}/{desiredReplicas}. Available replicas: {availableReplicas}/{desiredReplicas}.";
            return "Pending";
        }

        if (available is not null && !string.Equals(available.Status, "True", StringComparison.OrdinalIgnoreCase))
        {
            detail = string.IsNullOrWhiteSpace(available.Message)
                ? $"Available condition is {available.Status ?? "Unknown"}."
                : available.Message;
            return "Unavailable";
        }

        if (progressing is not null && !string.Equals(progressing.Status, "True", StringComparison.OrdinalIgnoreCase))
        {
            detail = string.IsNullOrWhiteSpace(progressing.Message)
                ? $"Progressing condition is {progressing.Status ?? "Unknown"}."
                : progressing.Message;
            return "Failed";
        }

        detail = $"Ready replicas: {readyReplicas}/{desiredReplicas}.";
        return "Ready";
    }

    private IReadOnlyList<string> ResolveExpectedDeployments()
    {
        var configured = options.ExpectedDeployments.Count > 0
            ? options.ExpectedDeployments
            : productContext.Current.ExpectedDeployments;

        return configured
            .Where(deployment => !string.IsNullOrWhiteSpace(deployment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> ResolveOptionalDeployments(IReadOnlyList<string> expectedDeployments)
    {
        var configured = options.OptionalDeployments.Count > 0
            ? options.OptionalDeployments
            : productContext.Current.OptionalDeployments;

        return configured
            .Where(deployment => !string.IsNullOrWhiteSpace(deployment))
            .Where(deployment => expectedDeployments.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<KubernetesDeploymentContainerInfo> GetContainerInfo(KubernetesDeploymentResource deployment)
    {
        return (deployment.Spec?.Template?.Spec?.Containers ?? [])
            .Where(container => !string.IsNullOrWhiteSpace(container.Name))
            .Select(container => new KubernetesDeploymentContainerInfo(
                container.Name!,
                string.IsNullOrWhiteSpace(container.Image) ? "Image not reported" : container.Image!))
            .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> ResolveTargetDeployments(
        KubernetesUpgradeScope scope,
        string? selectedDeployment,
        KubernetesNamespaceValidationResult validation,
        Dictionary<string, KubernetesDeploymentImageInfo> imageMap)
    {
        if (scope == KubernetesUpgradeScope.Individual)
        {
            if (string.IsNullOrWhiteSpace(selectedDeployment))
            {
                return [];
            }

            return validation.MatchedDeployments.Contains(selectedDeployment, StringComparer.OrdinalIgnoreCase) &&
                   imageMap.ContainsKey(selectedDeployment)
                ? [selectedDeployment]
                : [];
        }

        return validation.MatchedDeployments
            .Where(imageMap.ContainsKey)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<IReadOnlyList<KubernetesDeploymentUpgradeWorkItem>> ResolveDeploymentGroups(
        IReadOnlyList<KubernetesDeploymentUpgradeWorkItem> workItems)
    {
        var byName = workItems.ToDictionary(item => item.DeploymentName, StringComparer.OrdinalIgnoreCase);
        var grouped = new List<IReadOnlyList<KubernetesDeploymentUpgradeWorkItem>>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var deploymentGroup in productContext.Current.RolloutGroups)
        {
            AddGroup(deploymentGroup);
        }

        var remaining = workItems
            .Where(item => !assigned.Contains(item.DeploymentName))
            .OrderBy(item => item.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (remaining.Count > 0)
        {
            grouped.Add(remaining);
        }

        return grouped;

        void AddGroup(IReadOnlyList<string> deploymentNames)
        {
            var group = deploymentNames
                .Where(byName.ContainsKey)
                .Select(deploymentName => byName[deploymentName])
                .ToList();
            if (group.Count == 0)
            {
                return;
            }

            grouped.Add(group);
            foreach (var item in group)
            {
                assigned.Add(item.DeploymentName);
            }
        }
    }

    private static KubernetesContainer? SelectContainer(KubernetesDeploymentResource deployment, KubernetesDeploymentImageInfo releaseImage)
    {
        var containers = deployment.Spec?.Template?.Spec?.Containers ?? [];
        if (!string.IsNullOrWhiteSpace(releaseImage.ContainerName))
        {
            var namedContainer = containers.FirstOrDefault(container => string.Equals(container.Name, releaseImage.ContainerName, StringComparison.OrdinalIgnoreCase));
            if (namedContainer is not null)
            {
                return namedContainer;
            }
        }

        var releaseRepository = GetImageRepository(releaseImage.Image);
        var matchingContainer = containers.FirstOrDefault(container => string.Equals(GetImageRepository(container.Image), releaseRepository, StringComparison.OrdinalIgnoreCase));
        return matchingContainer ?? containers.FirstOrDefault();
    }

    private static string GetImageRepository(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return string.Empty;
        }

        var lastSlash = image.LastIndexOf('/');
        var lastColon = image.LastIndexOf(':');
        return lastColon > lastSlash ? image[..lastColon] : image;
    }

    private string BuildVersionApiUri(string selectedVersion, params string[] segments)
    {
        var product = productContext.Current;
        var baseUri = ResolveReleaseVersionsApiUrl(product).TrimEnd('/');
        var escapedVersion = Uri.EscapeDataString(selectedVersion.Trim());
        var suffix = string.Join("/", segments.Select(segment => Uri.EscapeDataString(segment.Trim('/'))));
        return $"{baseUri}/{escapedVersion}/{suffix}";
    }

    private string ResolveReleaseVersionsApiUrl(UpgradeProductDefinition product)
    {
        if (product.Product == UpgradeProduct.BoldBi &&
            !string.IsNullOrWhiteSpace(upgradeCenterOptions.BiReleaseVersionsApiUrl))
        {
            return upgradeCenterOptions.BiReleaseVersionsApiUrl;
        }

        if (product.Product == UpgradeProduct.BoldBi &&
            !string.IsNullOrWhiteSpace(upgradeCenterOptions.ReleaseVersionsApiUrl))
        {
            return upgradeCenterOptions.ReleaseVersionsApiUrl;
        }

        if (product.Product == UpgradeProduct.BoldReports &&
            !string.IsNullOrWhiteSpace(upgradeCenterOptions.ReportsReleaseVersionsApiUrl))
        {
            return upgradeCenterOptions.ReportsReleaseVersionsApiUrl;
        }

        return product.ReleaseVersionsApiUrl;
    }

    private static string? ResolveInClusterApiServer()
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = FirstConfiguredValue(
            Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS"),
            Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT"),
            "443");

        return $"https://{host}:{port}";
    }

    private static string? ReadFileIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path).Trim();
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record KubernetesRuntimeContext(bool Succeeded, string? Namespace, string? ApiServer, string? Token, string Message)
    {
        public static KubernetesRuntimeContext Failed(string? kubernetesNamespace, string message)
        {
            return new KubernetesRuntimeContext(false, kubernetesNamespace, null, null, message);
        }
    }

    private sealed record KubernetesRolloutHealthResult(bool Succeeded, bool IsTerminal, string Message)
    {
        public static KubernetesRolloutHealthResult Healthy(string message)
        {
            return new KubernetesRolloutHealthResult(true, true, message);
        }

        public static KubernetesRolloutHealthResult Waiting(string message)
        {
            return new KubernetesRolloutHealthResult(false, false, message);
        }

        public static KubernetesRolloutHealthResult Failed(string message)
        {
            return new KubernetesRolloutHealthResult(false, true, message);
        }
    }

    private sealed record KubernetesDeploymentUpgradeWorkItem(
        string DeploymentName,
        string ContainerName,
        string PreviousImage,
        string TargetImage);

    private sealed record KubernetesPreparedDeployment(
        KubernetesDeploymentUpgradeWorkItem? WorkItem,
        KubernetesDeploymentUpgradeResult? Result)
    {
        public static KubernetesPreparedDeployment Prepared(KubernetesDeploymentUpgradeWorkItem workItem)
        {
            return new KubernetesPreparedDeployment(workItem, null);
        }

        public static KubernetesPreparedDeployment Failed(KubernetesDeploymentUpgradeResult result)
        {
            return new KubernetesPreparedDeployment(null, result);
        }
    }

    private sealed class KubernetesReleaseImageResponse
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("deployments")]
        public List<KubernetesReleaseDeploymentImage>? Deployments { get; set; }
    }

    private sealed class KubernetesReleaseDeploymentImage
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("imageTag")]
        public string? ImageTag { get; set; }

        [JsonPropertyName("containerName")]
        public string? ContainerName { get; set; }
    }

    private sealed class KubernetesDeploymentList
    {
        [JsonPropertyName("items")]
        public List<KubernetesDeploymentResource>? Items { get; set; }
    }

    private sealed class KubernetesDeploymentResource
    {
        [JsonPropertyName("metadata")]
        public KubernetesMetadata? Metadata { get; set; }

        [JsonPropertyName("spec")]
        public KubernetesDeploymentSpec? Spec { get; set; }

        [JsonPropertyName("status")]
        public KubernetesDeploymentStatus? Status { get; set; }
    }

    private sealed class KubernetesMetadata
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("generation")]
        public long? Generation { get; set; }
    }

    private sealed class KubernetesDeploymentSpec
    {
        [JsonPropertyName("replicas")]
        public int? Replicas { get; set; }

        [JsonPropertyName("template")]
        public KubernetesPodTemplate? Template { get; set; }
    }

    private sealed class KubernetesDeploymentStatus
    {
        [JsonPropertyName("observedGeneration")]
        public long? ObservedGeneration { get; set; }

        [JsonPropertyName("replicas")]
        public int? Replicas { get; set; }

        [JsonPropertyName("updatedReplicas")]
        public int? UpdatedReplicas { get; set; }

        [JsonPropertyName("readyReplicas")]
        public int? ReadyReplicas { get; set; }

        [JsonPropertyName("availableReplicas")]
        public int? AvailableReplicas { get; set; }

        [JsonPropertyName("unavailableReplicas")]
        public int? UnavailableReplicas { get; set; }

        [JsonPropertyName("conditions")]
        public List<KubernetesDeploymentCondition>? Conditions { get; set; }
    }

    private sealed class KubernetesDeploymentCondition
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class KubernetesPodTemplate
    {
        [JsonPropertyName("spec")]
        public KubernetesPodSpec? Spec { get; set; }
    }

    private sealed class KubernetesPodSpec
    {
        [JsonPropertyName("containers")]
        public List<KubernetesContainer>? Containers { get; set; }
    }

    private sealed class KubernetesContainer
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> values)
        where T : class
    {
        return values.Where(value => value is not null).Select(value => value!);
    }
}
