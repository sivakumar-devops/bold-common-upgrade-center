namespace Bold.UpgradeCenter.Services;

public sealed class ProductHealthCheckOptions
{
    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 300;

    public int PollSeconds { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 10;
}

public sealed record ProductHealthCheckTarget(
    string OperationId,
    string Name,
    string DeploymentName,
    string RelativePath);

public sealed record ProductHealthCheckProgress(
    ProductHealthCheckTarget Target,
    UpgradeJobStageStatus Status,
    string Message);

public sealed record ProductHealthCheckResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<ProductHealthCheckProgress> Checks);

public interface IProductHealthCheckService
{
    IReadOnlyList<ProductHealthCheckTarget> GetRollbackHealthCheckTargets(IReadOnlyCollection<string> deploymentNames);

    Task<ProductHealthCheckResult> VerifyAsync(
        IReadOnlyList<ProductHealthCheckTarget> targets,
        Action<ProductHealthCheckProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProductHealthCheckService : IProductHealthCheckService
{
    private static readonly IReadOnlyList<ProductHealthCheckTarget> DefaultTargets =
    [
        new("health-id-web", "Identity Provider Web", "id-web-deployment", "health-check"),
        new("health-id-api", "Identity Provider API", "id-api-deployment", "api/health-check"),
        new("health-id-ums", "User Management Web", "id-ums-deployment", "ums/health-check"),
        new("health-bi-web", "Dashboard Server Web", "bi-web-deployment", "bi/health-check"),
        new("health-bi-api", "Dashboard Server API", "bi-api-deployment", "bi/api/health-check"),
        new("health-bi-jobs", "Dashboard Server Jobs", "bi-jobs-deployment", "bi/jobs/health-check"),
        new("health-bi-designer", "Dashboard Designer Service", "bi-dataservice-deployment", "bi/designer/health-check"),
        new("health-etl-service", "ETL Service", "bold-etl-deployment", "etlservice/health-check")
    ];

    private readonly HttpClient httpClient;
    private readonly IUpgradeCenterConfigurationProvider configurationProvider;
    private readonly IUpgradeProductContext productContext;
    private readonly ProductHealthCheckOptions options;
    private readonly ILogger<ProductHealthCheckService> logger;

    public ProductHealthCheckService(
        HttpClient httpClient,
        IUpgradeCenterConfigurationProvider configurationProvider,
        IUpgradeProductContext productContext,
        Microsoft.Extensions.Options.IOptions<ProductHealthCheckOptions> options,
        ILogger<ProductHealthCheckService> logger)
    {
        this.httpClient = httpClient;
        this.configurationProvider = configurationProvider;
        this.productContext = productContext;
        this.options = options.Value;
        this.logger = logger;
    }

    public IReadOnlyList<ProductHealthCheckTarget> GetRollbackHealthCheckTargets(IReadOnlyCollection<string> deploymentNames)
    {
        var selected = deploymentNames
            .Where(deployment => !string.IsNullOrWhiteSpace(deployment))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selected.Count == 0
            ? productContext.Current.HealthCheckTargets
            : productContext.Current.HealthCheckTargets
                .Where(target => selected.Contains(target.DeploymentName))
                .ToList();
    }

    public async Task<ProductHealthCheckResult> VerifyAsync(
        IReadOnlyList<ProductHealthCheckTarget> targets,
        Action<ProductHealthCheckProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new ProductHealthCheckResult(true, "Product health checks are disabled by configuration.", Array.Empty<ProductHealthCheckProgress>());
        }

        if (targets.Count == 0)
        {
            return new ProductHealthCheckResult(true, "No product health endpoints were applicable for the rolled-back deployments.", Array.Empty<ProductHealthCheckProgress>());
        }

        var runtime = await configurationProvider.GetConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(runtime.IdpBaseUrl))
        {
            return Failed(targets, "Product health verification failed because the IDP public URL could not be resolved.");
        }

        var pending = targets.ToDictionary(target => target.OperationId, target => target, StringComparer.OrdinalIgnoreCase);
        var completed = new List<ProductHealthCheckProgress>();
        var lastMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var timeout = TimeSpan.FromSeconds(Math.Max(30, options.TimeoutSeconds));
        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(options.PollSeconds, 2, 60));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        foreach (var target in targets)
        {
            progress?.Invoke(new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Running, "Waiting for service health endpoint to respond."));
        }

        while (pending.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var target in pending.Values.ToList())
            {
                var endpoint = CombineUrl(runtime.IdpBaseUrl, target.RelativePath);
                var check = await CheckEndpointAsync(target, endpoint, cancellationToken);
                if (check.Status == UpgradeJobStageStatus.Succeeded)
                {
                    pending.Remove(target.OperationId);
                    completed.Add(check);
                    progress?.Invoke(check);
                    continue;
                }

                lastMessages[target.OperationId] = check.Message;
                progress?.Invoke(check);
            }

            if (pending.Count > 0)
            {
                await Task.Delay(pollDelay, cancellationToken);
            }
        }

        foreach (var target in pending.Values)
        {
            var message = $"Health endpoint did not become healthy within {timeout.TotalSeconds:0} seconds. Last status: {lastMessages.GetValueOrDefault(target.OperationId, "No response was recorded.")}";
            var failed = new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Failed, message);
            completed.Add(failed);
            progress?.Invoke(failed);
        }

        var succeeded = completed.Count == targets.Count && completed.All(check => check.Status == UpgradeJobStageStatus.Succeeded);
        var resultMessage = succeeded
            ? $"Product health verification completed successfully for {completed.Count} endpoint(s)."
            : "Product health verification failed for one or more endpoints.";

        return new ProductHealthCheckResult(succeeded, resultMessage, completed);
    }

    private async Task<ProductHealthCheckProgress> CheckEndpointAsync(
        ProductHealthCheckTarget target,
        string endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, options.RequestTimeoutSeconds)));
            using var response = await httpClient.GetAsync(endpoint, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Succeeded, "Health endpoint responded successfully.");
            }

            return new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Running, $"Health endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Running, "Health endpoint request timed out.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Product health endpoint is not healthy yet. EndpointName: {EndpointName}.", target.Name);
            return new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Running, $"Health endpoint is not reachable: {exception.Message}");
        }
    }

    private static ProductHealthCheckResult Failed(IReadOnlyList<ProductHealthCheckTarget> targets, string message)
    {
        return new ProductHealthCheckResult(
            false,
            message,
            targets
                .Select(target => new ProductHealthCheckProgress(target, UpgradeJobStageStatus.Failed, message))
                .ToList());
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }
}
