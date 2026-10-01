using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public sealed class PlaywrightKubernetesJobService : IPlaywrightKubernetesJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SensitiveLinePattern = new(
        @"(?<key>password|pwd|secret|token|authorization|api[_-]?key|connectionstring)\s*[:=]\s*[""']?[^,""'\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string PlaywrightResourceAppName = "bold-upgrade-center-playwright";
    private const string PlaywrightJobIdAnnotation = "upgrade-center.bold.com/job-id";
    private const string PlaywrightValidationAnnotation = "upgrade-center.bold.com/validation";

    private readonly PlaywrightExecutionOptions playwrightOptions;
    private readonly KubernetesUpgradeOptions kubernetesOptions;
    private readonly IConfiguration configuration;
    private readonly IUpgradeCenterConfigurationProvider configurationProvider;
    private readonly IUpgradeDatabaseDiscoveryService databaseDiscoveryService;
    private readonly IUpgradeProductContext productContext;
    private readonly IPlaywrightReportStore reportStore;
    private readonly IPlaywrightReportUploadTokenStore reportUploadTokenStore;
    private readonly IPlaywrightValidationResultStore validationResultStore;
    private readonly IUpgradeOperationLogStore operationLogStore;
    private readonly IPlaywrightRunnerImageProvider runnerImageProvider;
    private readonly IHostEnvironment hostEnvironment;
    private readonly ILogger<PlaywrightKubernetesJobService> logger;

    public PlaywrightKubernetesJobService(
        IOptions<PlaywrightExecutionOptions> playwrightOptions,
        IOptions<KubernetesUpgradeOptions> kubernetesOptions,
        IConfiguration configuration,
        IUpgradeCenterConfigurationProvider configurationProvider,
        IUpgradeDatabaseDiscoveryService databaseDiscoveryService,
        IUpgradeProductContext productContext,
        IPlaywrightReportStore reportStore,
        IPlaywrightReportUploadTokenStore reportUploadTokenStore,
        IPlaywrightValidationResultStore validationResultStore,
        IUpgradeOperationLogStore operationLogStore,
        IPlaywrightRunnerImageProvider runnerImageProvider,
        IHostEnvironment hostEnvironment,
        ILogger<PlaywrightKubernetesJobService> logger)
    {
        this.playwrightOptions = playwrightOptions.Value;
        this.kubernetesOptions = kubernetesOptions.Value;
        this.configuration = configuration;
        this.configurationProvider = configurationProvider;
        this.databaseDiscoveryService = databaseDiscoveryService;
        this.productContext = productContext;
        this.reportStore = reportStore;
        this.reportUploadTokenStore = reportUploadTokenStore;
        this.validationResultStore = validationResultStore;
        this.operationLogStore = operationLogStore;
        this.runnerImageProvider = runnerImageProvider;
        this.hostEnvironment = hostEnvironment;
        this.logger = logger;
    }

    public async Task<PlaywrightScriptResult> RunAsync(
        PlaywrightValidationMode mode,
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var modeText = ToModeText(mode);
        var displayName = ToDisplayName(mode);

        progress?.Invoke(new PlaywrightValidationProgress("playwright-prepare", UpgradeJobStageStatus.Running, $"Preparing {ToProgressLabel(mode)}."));

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, context.Message, context.Message);
        }

        var runnerImage = await runnerImageProvider.GetRunnerImageAsync(runnerImageVersion ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(runnerImage))
        {
            var message = string.IsNullOrWhiteSpace(runnerImageVersion)
                ? $"Playwright runner image could not be resolved because the {ToProgressLabel(mode)} image version is not available."
                : $"Playwright runner image is not available from release metadata for {ToProgressLabel(mode)} version {runnerImageVersion}.";
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, message, message);
        }

        RuntimeConfiguration runtimeConfiguration;
        try
        {
            using var recoveryClient = CreateKubernetesHttpClient(context);
            var recoveryNames = CreatePlaywrightResourceNames(upgradeJobId, modeText);
            using var existingSecret = await GetOwnedResourceAsync(
                recoveryClient,
                $"/api/v1/namespaces/{Uri.EscapeDataString(context.Namespace!)}/secrets/{Uri.EscapeDataString(recoveryNames.SecretName)}",
                upgradeJobId,
                modeText,
                cancellationToken);

            string? existingToken = null;
            if (existingSecret is not null)
            {
                var existingValues = ReadSecretData(existingSecret.RootElement);
                runtimeConfiguration = new RuntimeConfiguration(
                    existingValues,
                    existingValues.Values.Where(value => value.Length >= 4).Distinct(StringComparer.Ordinal).ToList());
                existingValues.TryGetValue("BOLD_UPGRADE_CENTER_REPORT_UPLOAD_TOKEN", out existingToken);
            }
            else
            {
                runtimeConfiguration = await ResolveRuntimeConfigurationAsync(cancellationToken);
            }

            var reportUploadToken = reportUploadTokenStore.Register(upgradeJobId, mode, ResolveUploadTokenTtl(mode), existingToken);
            runtimeConfiguration = runtimeConfiguration.WithPlaywrightUploads(
                BuildReportUploadUrl(upgradeJobId, mode),
                BuildResultUploadUrl(upgradeJobId, mode),
                reportUploadToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"Playwright runtime configuration could not be resolved: {exception.Message}";
            logger.LogWarning(exception, "Playwright runtime configuration could not be resolved. No sensitive values were logged.");
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, message, message);
        }

        var resourceNames = CreatePlaywrightResourceNames(upgradeJobId, modeText);
        var secretName = resourceNames.SecretName;
        var jobName = resourceNames.JobName;
        var sharedStatePvcName = resourceNames.SharedStatePvcName;
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = PlaywrightResourceAppName,
            [PlaywrightJobIdAnnotation] = resourceNames.ShortJobId,
            [PlaywrightValidationAnnotation] = modeText
        };
        var annotations = new Dictionary<string, string>
        {
            [PlaywrightJobIdAnnotation] = upgradeJobId,
            [PlaywrightValidationAnnotation] = modeText
        };

        using var client = CreateKubernetesHttpClient(context);
        var secretCreated = false;
        var sharedStatePvcCreated = false;
        var jobCreated = false;
        PlaywrightScriptResult? executionResult = null;
        try
        {
            using var existingJob = await GetOwnedResourceAsync(
                client,
                $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(context.Namespace!)}/jobs/{Uri.EscapeDataString(jobName)}",
                upgradeJobId,
                modeText,
                cancellationToken);
            if (existingJob is not null &&
                existingJob.RootElement.GetProperty("metadata").TryGetProperty("creationTimestamp", out var timestamp) &&
                DateTimeOffset.TryParse(timestamp.GetString(), out var originalStart))
            {
                startedAt = originalStart;
            }

            if (mode == PlaywrightValidationMode.Cleanup)
            {
                await StopValidationBeforeCleanupAsync(client, context.Namespace!, upgradeJobId, cancellationToken);
            }

            if (playwrightOptions.SharedStateVolumeEnabled)
            {
                progress?.Invoke(new PlaywrightValidationProgress("playwright-shared-state", UpgradeJobStageStatus.Running, "Preparing shared Playwright state volume."));
                await CreateOrReuseSharedStatePvcAsync(client, context.Namespace!, sharedStatePvcName, labels, annotations, cancellationToken);
                sharedStatePvcCreated = true;
                progress?.Invoke(new PlaywrightValidationProgress("playwright-shared-state", UpgradeJobStageStatus.Succeeded, "Shared Playwright state volume is ready."));
                runtimeConfiguration = runtimeConfiguration.WithSharedStateDirectory(GetSharedStateMountPath());
            }

            progress?.Invoke(new PlaywrightValidationProgress("playwright-secret", UpgradeJobStageStatus.Running, "Creating temporary Playwright runtime Secret."));
            await CreateSecretAsync(client, context.Namespace!, secretName, labels, annotations, runtimeConfiguration.Environment, cancellationToken);
            secretCreated = true;
            progress?.Invoke(new PlaywrightValidationProgress("playwright-secret", UpgradeJobStageStatus.Succeeded, "Temporary runtime Secret created."));

            progress?.Invoke(new PlaywrightValidationProgress("playwright-job", UpgradeJobStageStatus.Running, $"Creating {displayName} Job."));
            await CreateJobAsync(client, context.Namespace!, jobName, secretName, sharedStatePvcName, labels, annotations, modeText, runnerImage, runtimeConfiguration.Environment.Keys, mode, cancellationToken);
            jobCreated = true;
            progress?.Invoke(new PlaywrightValidationProgress("playwright-job", UpgradeJobStageStatus.Succeeded, $"{displayName} Job created."));

            progress?.Invoke(new PlaywrightValidationProgress("playwright-runner", UpgradeJobStageStatus.Running, "Waiting for Playwright runner pod."));
            executionResult = await MonitorJobAsync(
                client,
                context.Namespace!,
                jobName,
                labels,
                mode,
                upgradeJobId,
                runtimeConfiguration.RedactionValues,
                startedAt,
                displayName,
                progress,
                cancellationToken);

            return executionResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failureMessage = exception is KubernetesApiRequestException kubernetesException
                ? kubernetesException.PublicMessage
                : exception.Message;
            var message = $"Playwright validation infrastructure failed: {failureMessage}";
            var technicalError = exception is KubernetesApiRequestException kubernetesApiException
                ? kubernetesApiException.DetailedMessage
                : message;
            logger.LogWarning(exception, "Playwright Kubernetes Job execution failed. JobName: {JobName}. No sensitive values were logged.", jobName);
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, technicalError, message, jobCreated);
        }
        finally
        {
            string? cleanupResourceError = null;
            reportUploadTokenStore.Revoke(upgradeJobId, mode);
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await DeleteJobAsync(client, context.Namespace!, jobName, CancellationToken.None);
                    logger.LogInformation("Playwright validation Job deleted after cancellation. JobName: {JobName}.", jobName);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Playwright validation Job cleanup failed after cancellation. JobName: {JobName}.", jobName);
                }
            }

            if (secretCreated)
            {
                try
                {
                    await DeleteSecretAsync(client, context.Namespace!, secretName, CancellationToken.None);
                    logger.LogInformation("Temporary Playwright Secret deleted. SecretName: {SecretName}.", secretName);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Temporary Playwright Secret cleanup failed. SecretName: {SecretName}.", secretName);
                    if (mode == PlaywrightValidationMode.Cleanup)
                    {
                        cleanupResourceError = $"Temporary runtime Secret '{secretName}' could not be removed.";
                    }
                }
            }

            if (cleanupResourceError is null && ShouldDeleteSharedStatePvc(mode, executionResult, sharedStatePvcCreated, jobCreated))
            {
                try
                {
                    await DeletePersistentVolumeClaimAsync(client, context.Namespace!, sharedStatePvcName, CancellationToken.None);
                    logger.LogInformation("Temporary Playwright shared state PVC deleted. PersistentVolumeClaimName: {PersistentVolumeClaimName}.", sharedStatePvcName);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Temporary Playwright shared state PVC cleanup failed. PersistentVolumeClaimName: {PersistentVolumeClaimName}.", sharedStatePvcName);
                    if (mode == PlaywrightValidationMode.Cleanup)
                    {
                        cleanupResourceError = $"Shared state PVC '{sharedStatePvcName}' could not be removed.";
                    }
                }
            }

            if (cleanupResourceError is not null)
            {
                throw new InvalidOperationException(cleanupResourceError + " Manual cleanup is required. Review the application logs for the Kubernetes failure reason.");
            }
        }
    }

    private async Task StopValidationBeforeCleanupAsync(
        HttpClient client,
        string kubernetesNamespace,
        string jobId,
        CancellationToken cancellationToken)
    {
        foreach (var validationMode in new[] { "pre", "post" })
        {
            var names = CreatePlaywrightResourceNames(jobId, validationMode);
            using var job = await GetOwnedResourceAsync(
                client,
                $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs/{Uri.EscapeDataString(names.JobName)}",
                jobId,
                validationMode,
                cancellationToken);
            if (job is not null && GetJobTerminalCondition(job.RootElement) is not ("Complete" or "Failed"))
            {
                await DeleteJobAsync(client, kubernetesNamespace, names.JobName, cancellationToken);
            }

            var selector = Uri.EscapeDataString($"{PlaywrightJobIdAnnotation}={names.ShortJobId},{PlaywrightValidationAnnotation}={validationMode}");
            var stopped = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                using var pods = await GetJsonAsync(
                    client,
                    $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods?labelSelector={selector}",
                    cancellationToken);
                stopped = pods.RootElement.GetProperty("items").EnumerateArray().All(pod =>
                    TryGetString(pod, "status", "phase") is "Succeeded" or "Failed");
                if (stopped)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            if (!stopped)
            {
                throw new InvalidOperationException("Cleanup cannot start because a validation pod has not stopped. Resource deletion cannot be verified safely.");
            }
        }
    }

    public async Task CleanupSharedStateAsync(string upgradeJobId, CancellationToken cancellationToken = default)
    {
        if (!playwrightOptions.SharedStateVolumeEnabled)
        {
            return;
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            logger.LogWarning(
                "Playwright shared state cleanup skipped because Kubernetes context could not be resolved. JobId: {JobId}. Message: {Message}",
                upgradeJobId,
                context.Message);
            return;
        }

        using var client = CreateKubernetesHttpClient(context);
        var pvcName = GetSharedStatePvcName(upgradeJobId);
        await DeletePersistentVolumeClaimAsync(client, context.Namespace!, pvcName, cancellationToken);
    }

    private async Task DeleteJobAsync(HttpClient client, string kubernetesNamespace, string jobName, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(
            $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs/{Uri.EscapeDataString(jobName)}?propagationPolicy=Foreground",
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<RuntimeConfiguration> ResolveRuntimeConfigurationAsync(CancellationToken cancellationToken)
    {
        return productContext.Current.Product == UpgradeProduct.BoldReports
            ? await ResolveBoldReportsRuntimeConfigurationAsync(cancellationToken)
            : await ResolveBoldBiRuntimeConfigurationAsync(cancellationToken);
    }

    private async Task<RuntimeConfiguration> ResolveBoldBiRuntimeConfigurationAsync(CancellationToken cancellationToken)
    {
        var runtime = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var masterDatabase = await databaseDiscoveryService.DiscoverMasterDatabaseAsync(cancellationToken);
        var databaseValues = ParseDatabaseValues(masterDatabase);
        var adminUsername = ResolveAdminUsername();
        var adminPassword = ResolveAdminPassword();
        var passRateThreshold = ResolvePassRateThreshold();
        var workerCount = ResolveWorkerCount();
        var databaseType = FirstConfiguredValue(
            playwrightOptions.DbType,
            configuration["UpgradeCenter:Playwright:DbType"],
            configuration["BOLDBI_DB_TYPE"],
            databaseValues.DatabaseType)!;
        var databaseSsl = await ResolveDatabaseSslAsync(
            masterDatabase,
            databaseValues.SslDisposition,
            cancellationToken);
        var showDataHub = ResolveShowDataHub(runtime.ShowDataHub);
        var databaseMaintenance = FirstConfiguredValue(
            playwrightOptions.DbMaintenance,
            configuration["UpgradeCenter:Playwright:DbMaintenance"],
            configuration["BOLDBI_DB_MAINTENANCE"]);
        var databaseAdditionalParameters = FirstConfiguredValue(
            playwrightOptions.DbAdditionalParameters,
            configuration["UpgradeCenter:Playwright:DbAdditionalParameters"],
            configuration["BOLDBI_DB_ADDITIONAL_PARAMETERS"]);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BOLDBI_BASE_URL"] = Required(runtime.IdpBaseUrl, "IDP public base URL"),
            ["BOLDBI_ADMIN_USERNAME"] = Required(adminUsername, "Playwright admin username"),
            ["BOLDBI_ADMIN_PASSWORD"] = Required(adminPassword, "Playwright admin password"),
            ["BOLDBI_API_KEY_ID"] = Required(runtime.InternalAppClientId, "internal app client ID"),
            ["BOLDBI_API_KEY_SECRET"] = Required(runtime.InternalAppClientSecret, "internal app client secret"),
            ["BOLDBI_SHOW_DATA_HUB"] = showDataHub ? "true" : "false",
            ["BOLDBI_DB_HOST"] = Required(FirstConfiguredValue(playwrightOptions.DbHost, configuration["UpgradeCenter:Playwright:DbHost"], configuration["BOLDBI_DB_HOST"], databaseValues.Host), "database host"),
            ["BOLDBI_DB_PORT"] = Required(FirstConfiguredValue(playwrightOptions.DbPort, configuration["UpgradeCenter:Playwright:DbPort"], configuration["BOLDBI_DB_PORT"], databaseValues.Port), "database port"),
            ["BOLDBI_DB_TYPE"] = databaseType,
            ["BOLDBI_DB_USERNAME"] = Required(FirstConfiguredValue(playwrightOptions.DbUsername, configuration["UpgradeCenter:Playwright:DbUsername"], configuration["BOLDBI_DB_USERNAME"], databaseValues.Username), "database username"),
            ["BOLDBI_DB_PASSWORD"] = Required(FirstConfiguredValue(playwrightOptions.DbPassword, configuration["UpgradeCenter:Playwright:DbPassword"], configuration["BOLDBI_DB_PASSWORD"], databaseValues.Password), "database password"),
            ["BOLDBI_DB_SSL"] = databaseSsl ? "true" : "false",
            ["BOLDBI_PLAYWRIGHT_PASS_THRESHOLD"] = passRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BOLDBI_PLAYWRIGHT_WORKERS"] = workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PLAYWRIGHT_PRE_JUNIT_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PreJunitReportPath",
                playwrightOptions.PreJunitReportPath),
            ["PLAYWRIGHT_POST_JUNIT_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PostJunitReportPath",
                playwrightOptions.PostJunitReportPath),
            ["PLAYWRIGHT_PRE_HTML_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PreHtmlReportPath",
                playwrightOptions.PreHtmlReportPath),
            ["PLAYWRIGHT_POST_HTML_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PostHtmlReportPath",
                playwrightOptions.PostHtmlReportPath),
            ["PLAYWRIGHT_PRE_HTML_REPORT_DIR"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PreHtmlReportDirectory",
                playwrightOptions.PreHtmlReportDirectory),
            ["PLAYWRIGHT_POST_HTML_REPORT_DIR"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PostHtmlReportDirectory",
                playwrightOptions.PostHtmlReportDirectory)
        };

        AddOptional(values, "BOLDBI_DB_MAINTENANCE", databaseMaintenance);
        AddOptional(values, "BOLDBI_DB_ADDITIONAL_PARAMETERS", databaseAdditionalParameters);
        AddOptional(values, "PLAYWRIGHT_POD_HOLD_SECONDS");

        var redactionValues = values.Values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new RuntimeConfiguration(values, redactionValues);
    }

    private async Task<RuntimeConfiguration> ResolveBoldReportsRuntimeConfigurationAsync(CancellationToken cancellationToken)
    {
        var runtime = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var masterDatabase = await databaseDiscoveryService.DiscoverMasterDatabaseAsync(cancellationToken);
        var databaseValues = ParseDatabaseValues(masterDatabase);
        var databaseSsl = await ResolveDatabaseSslAsync(
            masterDatabase,
            databaseValues.SslDisposition,
            "BOLDREPORTS_DB_SSL",
            cancellationToken);
        var adminUsername = ResolveAdminUsername();
        var adminPassword = ResolveAdminPassword();
        var passRateThreshold = ResolvePassRateThreshold("BOLDREPORTS_PLAYWRIGHT_PASS_THRESHOLD", "100");
        var workerCount = ResolveWorkerCount("BOLDREPORTS_PLAYWRIGHT_WORKERS", "4");
        var databaseHost = Required(databaseValues.Host, "database host");
        var databasePort = Required(databaseValues.Port, "database port");
        var databaseName = Required(databaseValues.DatabaseName, "database name");
        var databaseUsername = Required(databaseValues.Username, "database username");
        var databasePassword = Required(databaseValues.Password, "database password");
        var siteDatabaseName = FirstConfiguredValue(
            configuration["UpgradeCenter:Playwright:SiteDatabaseName"],
            configuration["BOLDREPORTS_SITE_DATABASE_NAME"])
            ?? string.Empty;
        var singleDbConfiguration = FirstConfiguredValue(
            configuration["UpgradeCenter:Playwright:SingleDbConfiguration"],
            configuration["BOLDREPORTS_SINGLE_DB_CONFIGURATION"],
            (runtime.UseSingleTenantDb ?? true) ? "true" : "false");

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BOLDREPORTS_BASE_URL"] = Required(runtime.IdpBaseUrl, "IDP public base URL"),
            ["BOLDREPORTS_ADMIN_USERNAME"] = Required(adminUsername, "Playwright admin username"),
            ["BOLDREPORTS_ADMIN_PASSWORD"] = Required(adminPassword, "Playwright admin password"),
            ["BOLDREPORTS_DB_HOST"] = databaseHost,
            ["BOLDREPORTS_DB_PORT"] = databasePort,
            ["BOLDREPORTS_DB_NAME"] = databaseName,
            ["BOLDREPORTS_SITE_DATABASE_NAME"] = siteDatabaseName,
            ["BOLDREPORTS_DB_TYPE"] = databaseValues.DatabaseType,
            ["BOLDREPORTS_DB_USERNAME"] = databaseUsername,
            ["BOLDREPORTS_DB_PASSWORD"] = databasePassword,
            ["BOLDREPORTS_DB_SSL"] = databaseSsl ? "true" : "false",
            ["BOLDREPORTS_SINGLE_DB_CONFIGURATION"] = Required(singleDbConfiguration, "Playwright single database configuration"),
            ["BOLDREPORTS_PLAYWRIGHT_PASS_THRESHOLD"] = passRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BOLDREPORTS_PLAYWRIGHT_WORKERS"] = workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PLAYWRIGHT_PRE_JUNIT_REPORT_PATH"] = "/app/Upgrade-results/pre-upgrade-test-results/testresults.json",
            ["PLAYWRIGHT_POST_JUNIT_REPORT_PATH"] = "/app/Upgrade-results/post-upgrade-test-results/testresults.json",
            ["PLAYWRIGHT_PRE_HTML_REPORT_PATH"] = "/app/Upgrade-results/pre-upgrade-test-results/index.html",
            ["PLAYWRIGHT_POST_HTML_REPORT_PATH"] = "/app/Upgrade-results/post-upgrade-test-results/index.html",
            ["PLAYWRIGHT_PRE_HTML_REPORT_DIR"] = "/app/Upgrade-results/pre-upgrade-test-results",
            ["PLAYWRIGHT_POST_HTML_REPORT_DIR"] = "/app/Upgrade-results/post-upgrade-test-results",
            ["BOLDREPORTS_DEDICATED_CLEANUP"] = "true"
        };

        if (!string.IsNullOrWhiteSpace(runtime.InternalAppClientId))
        {
            values["BOLDREPORTS_API_KEY_ID"] = runtime.InternalAppClientId;
        }

        if (!string.IsNullOrWhiteSpace(runtime.InternalAppClientSecret))
        {
            values["BOLDREPORTS_API_KEY_SECRET"] = runtime.InternalAppClientSecret;
        }

        AddOptional(values, "BOLDREPORTS_SITE_NAME");
        AddOptional(values, "BOLDREPORTS_SITE_IDENTIFIER");
        AddOptional(values, "PLAYWRIGHT_POD_HOLD_SECONDS");

        var redactionValues = values.Values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new RuntimeConfiguration(values, redactionValues);
    }

    private int ResolvePassRateThreshold()
    {
        return ResolvePassRateThreshold("BOLDBI_PLAYWRIGHT_PASS_THRESHOLD", "100");
    }

    private string? ResolveAdminUsername()
    {
        return FirstConfiguredValue(
            playwrightOptions.AdminUsername,
            configuration["UpgradeCenter:Playwright:AdminUsername"],
            configuration["BOLD_ADMIN_USERNAME"]);
    }

    private string? ResolveAdminPassword()
    {
        return FirstConfiguredValue(
            playwrightOptions.AdminPassword,
            configuration["UpgradeCenter:Playwright:AdminPassword"],
            configuration["BOLD_ADMIN_PASSWORD"]);
    }

    private int ResolvePassRateThreshold(string environmentVariableName, string defaultValue)
    {
        var rawValue = FirstConfiguredValue(
            configuration[environmentVariableName],
            configuration["UpgradeCenter:Playwright:PassRateThreshold"],
            playwrightOptions.PassRateThreshold,
            defaultValue);

        if (!int.TryParse(rawValue, out var threshold) || threshold < 0 || threshold > 100)
        {
            throw new InvalidOperationException("Playwright pass-rate threshold must be configured as a number between 0 and 100.");
        }

        return threshold;
    }

    private int ResolveWorkerCount()
    {
        return ResolveWorkerCount("BOLDBI_PLAYWRIGHT_WORKERS", "1");
    }

    private int ResolveWorkerCount(string environmentVariableName, string defaultValue)
    {
        var rawValue = FirstConfiguredValue(
            configuration[environmentVariableName],
            configuration["UpgradeCenter:Playwright:WorkerCount"],
            playwrightOptions.WorkerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            defaultValue);

        if (!int.TryParse(rawValue, out var workers) || workers < 1)
        {
            throw new InvalidOperationException("Playwright worker count must be configured as a positive number.");
        }

        return workers;
    }

    private async Task<bool> ResolveDatabaseSslAsync(
        DiscoveredDatabaseConnection masterDatabase,
        DatabaseSslDisposition discoveredDisposition,
        CancellationToken cancellationToken)
    {
        return await ResolveDatabaseSslAsync(
            masterDatabase,
            discoveredDisposition,
            "BOLDBI_DB_SSL",
            cancellationToken);
    }

    private async Task<bool> ResolveDatabaseSslAsync(
        DiscoveredDatabaseConnection masterDatabase,
        DatabaseSslDisposition discoveredDisposition,
        string environmentVariableName,
        CancellationToken cancellationToken)
    {
        var configuredValue = FirstConfiguredValue(
            configuration[environmentVariableName],
            playwrightOptions.DbSsl?.ToString(),
            configuration["UpgradeCenter:Playwright:DbSsl"]);

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" => true,
                "false" or "0" or "no" or "n" => false,
                _ => throw new InvalidOperationException("Playwright database SSL value must be configured as true or false.")
            };
        }

        if (discoveredDisposition != DatabaseSslDisposition.Ambiguous)
        {
            return discoveredDisposition == DatabaseSslDisposition.Enabled;
        }

        try
        {
            var encrypted = await DetectDatabaseEncryptionAsync(masterDatabase, cancellationToken);
            logger.LogInformation(
                "Master database transport encryption was detected for an ambiguous SSL mode. DatabaseType: {DatabaseType}. Encrypted: {Encrypted}.",
                masterDatabase.DatabaseType,
                encrypted);
            return encrypted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            logger.LogWarning(
                "Unable to determine master database transport encryption for an ambiguous SSL mode. DatabaseType: {DatabaseType}. No connection details were logged.",
                masterDatabase.DatabaseType);
            throw new InvalidOperationException(
                "Unable to determine whether the master database connection is encrypted. Review the database SSL configuration.");
        }
    }

    private static Task<bool> DetectDatabaseEncryptionAsync(
        DiscoveredDatabaseConnection masterDatabase,
        CancellationToken cancellationToken)
    {
        return masterDatabase.DatabaseType switch
        {
            DatabaseType.PostgreSQL => DetectPostgreSqlEncryptionAsync(masterDatabase.ConnectionString, cancellationToken),
            DatabaseType.MySQL => DetectMySqlEncryptionAsync(masterDatabase.ConnectionString, cancellationToken),
            DatabaseType.MSSQL => DetectSqlServerEncryptionAsync(masterDatabase.ConnectionString, cancellationToken),
            DatabaseType.Oracle => DetectOracleEncryptionAsync(masterDatabase.ConnectionString, cancellationToken),
            _ => throw new NotSupportedException($"Database type '{masterDatabase.DatabaseType}' is not supported for SSL detection.")
        };
    }

    private static async Task<bool> DetectPostgreSqlEncryptionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid();",
            connection)
        {
            CommandTimeout = 10
        };
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool encrypted
            ? encrypted
            : throw new InvalidOperationException("PostgreSQL did not return the active connection encryption state.");
    }

    private static async Task<bool> DetectMySqlEncryptionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SHOW SESSION STATUS LIKE 'Ssl_cipher';", connection)
        {
            CommandTimeout = 10
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.FieldCount < 2)
        {
            throw new InvalidOperationException("MySQL did not return the active connection encryption state.");
        }

        var cipher = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return !string.IsNullOrWhiteSpace(cipher);
    }

    private static async Task<bool> DetectSqlServerEncryptionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id = @@SPID;",
            connection)
        {
            CommandTimeout = 10
        };
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException("SQL Server did not return the active connection encryption state.");
    }

    private static async Task<bool> DetectOracleEncryptionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OracleCommand(
            "SELECT SYS_CONTEXT('USERENV', 'NETWORK_PROTOCOL') FROM DUAL",
            connection)
        {
            CommandTimeout = 10
        };
        var protocol = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))?.Trim();
        if (string.Equals(protocol, "tcps", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException("Oracle did not return a recognized active connection protocol.");
    }

    private bool ResolveShowDataHub(bool? runtimeValue)
    {
        if (runtimeValue.HasValue)
        {
            return runtimeValue.Value;
        }

        var configuredValue = FirstConfiguredValue(
            configuration["UpgradeCenter:Playwright:ShowDataHub"],
            configuration["BOLDBI_SHOW_DATA_HUB"]);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            logger.LogWarning("ShowDataHub was not available from IDP configuration. BOLDBI_SHOW_DATA_HUB will default to false for the Playwright runner.");
            return false;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" => true,
            "false" or "0" or "no" or "n" => false,
            _ => LogInvalidShowDataHubAndDefault(configuredValue)
        };
    }

    private bool LogInvalidShowDataHubAndDefault(string configuredValue)
    {
        logger.LogWarning("ShowDataHub value '{ShowDataHubValue}' is invalid. BOLDBI_SHOW_DATA_HUB will default to false for the Playwright runner.", configuredValue);
        return false;
    }

    private string GetConfiguredReportPath(string key, string configuredValue)
    {
        var value = FirstConfiguredValue(configuration[key], configuredValue);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string ToModeText(PlaywrightValidationMode mode)
    {
        return mode switch
        {
            PlaywrightValidationMode.Post => "post",
            PlaywrightValidationMode.Cleanup => "cleanup",
            _ => "pre"
        };
    }

    private static string ToDisplayName(PlaywrightValidationMode mode)
    {
        return mode switch
        {
            PlaywrightValidationMode.Post => "Post-upgrade Playwright validation",
            PlaywrightValidationMode.Cleanup => "Playwright cleanup",
            _ => "Pre-upgrade Playwright validation"
        };
    }

    private static string ToProgressLabel(PlaywrightValidationMode mode)
    {
        return mode switch
        {
            PlaywrightValidationMode.Post => "post-upgrade validation",
            PlaywrightValidationMode.Cleanup => "Playwright cleanup",
            _ => "pre-upgrade validation"
        };
    }

    private int GetJobTimeoutSeconds(PlaywrightValidationMode mode)
    {
        if (mode == PlaywrightValidationMode.Cleanup)
        {
            var configured = FirstConfiguredValue(
                configuration["UpgradeCenter:Playwright:CleanupJobTimeoutSeconds"],
                playwrightOptions.CleanupJobTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return int.TryParse(configured, out var cleanupTimeout)
                ? cleanupTimeout
                : playwrightOptions.CleanupJobTimeoutSeconds;
        }

        return playwrightOptions.JobTimeoutSeconds;
    }

    private TimeSpan ResolveUploadTokenTtl(PlaywrightValidationMode mode)
    {
        var timeoutSeconds = Math.Max(300, GetJobTimeoutSeconds(mode));
        return TimeSpan.FromSeconds(timeoutSeconds + 600);
    }

    private void AddOptional(IDictionary<string, string> values, string key)
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    public Task<PlaywrightScriptResult> RunCleanupAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(PlaywrightValidationMode.Cleanup, upgradeJobId, progress, runnerImageVersion, cancellationToken);
    }

    private static void AddOptional(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    private DatabaseRuntimeValues ParseDatabaseValues(DiscoveredDatabaseConnection database)
    {
        return database.DatabaseType switch
        {
            DatabaseType.PostgreSQL => ParsePostgreSql(database.ConnectionString),
            DatabaseType.MySQL => ParseMySql(database.ConnectionString),
            DatabaseType.MSSQL => ParseSqlServer(database.ConnectionString),
            DatabaseType.Oracle => ParseOracle(database.ConnectionString),
            _ => throw new NotSupportedException($"Database type '{database.DatabaseType}' is not supported for Playwright runtime configuration.")
        };
    }

    private static DatabaseRuntimeValues ParsePostgreSql(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var sslDisposition = builder.SslMode switch
        {
            SslMode.Disable => DatabaseSslDisposition.Disabled,
            SslMode.Allow or SslMode.Prefer => DatabaseSslDisposition.Ambiguous,
            _ => DatabaseSslDisposition.Enabled
        };
        return new DatabaseRuntimeValues(
            builder.Host,
            builder.Port.ToString(),
            builder.Database,
            "PostgreSQL",
            builder.Username,
            builder.Password,
            sslDisposition);
    }

    private static DatabaseRuntimeValues ParseMySql(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var sslDisposition = builder.SslMode switch
        {
            MySqlSslMode.None => DatabaseSslDisposition.Disabled,
            MySqlSslMode.Preferred => DatabaseSslDisposition.Ambiguous,
            _ => DatabaseSslDisposition.Enabled
        };
        return new DatabaseRuntimeValues(
            builder.Server,
            builder.Port.ToString(),
            builder.Database,
            "MySQL",
            builder.UserID,
            builder.Password,
            sslDisposition);
    }

    private static DatabaseRuntimeValues ParseSqlServer(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var (host, port) = SplitSqlServerDataSource(builder.DataSource);
        return new DatabaseRuntimeValues(
            host,
            port,
            builder.InitialCatalog,
            "MSSQL",
            builder.UserID,
            builder.Password,
            builder.Encrypt
                ? DatabaseSslDisposition.Enabled
                : DatabaseSslDisposition.Ambiguous);
    }

    private static DatabaseRuntimeValues ParseOracle(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        var (host, port) = SplitOracleDataSource(dataSource);
        return new DatabaseRuntimeValues(
            host,
            port,
            string.IsNullOrWhiteSpace(dataSource) ? builder.UserID : dataSource,
            "Oracle",
            builder.UserID,
            builder.Password,
            ResolveOracleSslDisposition(dataSource));
    }

    private static DatabaseSslDisposition ResolveOracleSslDisposition(string dataSource)
    {
        if (Regex.IsMatch(dataSource, @"(?:PROTOCOL\s*=\s*TCPS\b)|(?:^TCPS://)", RegexOptions.IgnoreCase))
        {
            return DatabaseSslDisposition.Enabled;
        }

        if (Regex.IsMatch(dataSource, @"(?:PROTOCOL\s*=\s*TCP\b)|(?:^TCP://)", RegexOptions.IgnoreCase))
        {
            return DatabaseSslDisposition.Disabled;
        }

        return DatabaseSslDisposition.Ambiguous;
    }

    private static (string Host, string Port) SplitSqlServerDataSource(string dataSource)
    {
        var normalized = dataSource.Trim();
        if (normalized.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        var parts = normalized.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (normalized, "1433");
    }

    private static (string Host, string Port) SplitOracleDataSource(string dataSource)
    {
        var match = Regex.Match(dataSource, @"(?<host>[^/:]+):(?<port>\d+)");
        return match.Success
            ? (match.Groups["host"].Value, match.Groups["port"].Value)
            : (dataSource, "1521");
    }

    private async Task CreateSecretAsync(
        HttpClient client,
        string kubernetesNamespace,
        string secretName,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> annotations,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            apiVersion = "v1",
            kind = "Secret",
            metadata = new
            {
                name = secretName,
                labels,
                annotations
            },
            type = "Opaque",
            stringData = values
        };

        using var response = await client.PostAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/secrets",
            ToJsonContent(body),
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            using var existing = await GetOwnedResourceAsync(
                client,
                $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/secrets/{Uri.EscapeDataString(secretName)}",
                annotations[PlaywrightJobIdAnnotation],
                annotations[PlaywrightValidationAnnotation],
                cancellationToken);
            if (existing is not null)
            {
                return;
            }
        }

        await EnsureKubernetesApiSuccessAsync(
            response,
            "creating Playwright runtime Secret",
            secretName,
            cancellationToken);
    }

    private async Task CreateOrReuseSharedStatePvcAsync(
        HttpClient client,
        string kubernetesNamespace,
        string pvcName,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> annotations,
        CancellationToken cancellationToken)
    {
        var spec = new Dictionary<string, object?>
        {
            ["accessModes"] = new[] { GetSharedStateAccessMode() },
            ["resources"] = new
            {
                requests = new
                {
                    storage = GetSharedStateStorageSize()
                }
            }
        };

        var storageClassName = FirstConfiguredValue(
            playwrightOptions.SharedStateStorageClassName,
            configuration["UpgradeCenter:Playwright:SharedStateStorageClassName"]);
        if (!string.IsNullOrWhiteSpace(storageClassName))
        {
            spec["storageClassName"] = storageClassName.Trim();
        }

        var body = new
        {
            apiVersion = "v1",
            kind = "PersistentVolumeClaim",
            metadata = new
            {
                name = pvcName,
                labels,
                annotations
            },
            spec
        };

        using var response = await client.PostAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/persistentvolumeclaims",
            ToJsonContent(body),
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return;
        }

        await EnsureKubernetesApiSuccessAsync(
            response,
            "creating Playwright shared state PersistentVolumeClaim",
            pvcName,
            cancellationToken);
    }

    private async Task CreateJobAsync(
        HttpClient client,
        string kubernetesNamespace,
        string jobName,
        string secretName,
        string sharedStatePvcName,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> annotations,
        string mode,
        string runnerImage,
        IEnumerable<string> secretKeys,
        PlaywrightValidationMode validationMode,
        CancellationToken cancellationToken)
    {
        var env = secretKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select(key => new
            {
                name = key,
                valueFrom = new
                {
                    secretKeyRef = new
                    {
                        name = secretName,
                        key
                    }
                }
            })
            .ToArray();

        object?[] volumeMounts = playwrightOptions.SharedStateVolumeEnabled
            ? new object?[]
            {
                new
                {
                    name = "playwright-shared-state",
                    mountPath = GetSharedStateMountPath()
                }
            }
            : Array.Empty<object?>();

        object?[] volumes = playwrightOptions.SharedStateVolumeEnabled
            ? new object?[]
            {
                new
                {
                    name = "playwright-shared-state",
                    persistentVolumeClaim = new
                    {
                        claimName = sharedStatePvcName
                    }
                }
            }
            : Array.Empty<object?>();

        var containerSpec = new Dictionary<string, object?>
        {
            ["name"] = "playwright-runner",
            ["image"] = runnerImage,
            ["imagePullPolicy"] = "IfNotPresent",
            ["args"] = new[] { mode },
            ["env"] = env,
            ["volumeMounts"] = volumeMounts
        };

        var containerResources = BuildContainerResources();
        if (containerResources is not null)
        {
            containerSpec["resources"] = containerResources;
        }

        var podSpec = new Dictionary<string, object?>
        {
            ["restartPolicy"] = "Never",
            ["containers"] = new object[] { containerSpec }
        };

        if (playwrightOptions.SharedStateVolumeEnabled)
        {
            var sharedStateFsGroup = GetSharedStateFsGroup();
            if (sharedStateFsGroup.HasValue)
            {
                podSpec["securityContext"] = new
                {
                    fsGroup = sharedStateFsGroup.Value
                };
            }
        }

        if (volumes.Length > 0)
        {
            podSpec["volumes"] = volumes;
        }

        var spec = new Dictionary<string, object?>
        {
            ["backoffLimit"] = Math.Max(0, playwrightOptions.BackoffLimit),
            ["activeDeadlineSeconds"] = Math.Max(300, GetJobTimeoutSeconds(validationMode)),
            ["template"] = new
            {
                metadata = new
                {
                    labels,
                    annotations
                },
                spec = podSpec
            }
        };

        if (playwrightOptions.CompletedJobTtlSeconds.HasValue)
        {
            spec["ttlSecondsAfterFinished"] = Math.Max(0, playwrightOptions.CompletedJobTtlSeconds.Value);
        }

        var body = new
        {
            apiVersion = "batch/v1",
            kind = "Job",
            metadata = new
            {
                name = jobName,
                labels,
                annotations
            },
            spec
        };

        using var response = await client.PostAsync(
            $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs",
            ToJsonContent(body),
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            using var existing = await GetOwnedResourceAsync(
                client,
                $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs/{Uri.EscapeDataString(jobName)}",
                annotations[PlaywrightJobIdAnnotation],
                mode,
                cancellationToken);
            if (existing is not null)
            {
                return;
            }
        }

        await EnsureKubernetesApiSuccessAsync(
            response,
            "creating Playwright Kubernetes Job",
            jobName,
            cancellationToken);
    }

    private async Task<JsonDocument?> GetOwnedResourceAsync(
        HttpClient client,
        string url,
        string jobId,
        string mode,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureKubernetesApiSuccessAsync(response, "reading existing Playwright resource", jobId, cancellationToken);
        var resource = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (resource.RootElement.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("annotations", out var annotations) &&
            annotations.TryGetProperty(PlaywrightJobIdAnnotation, out var owner) &&
            owner.GetString() == jobId &&
            annotations.TryGetProperty(PlaywrightValidationAnnotation, out var validation) &&
            validation.GetString() == mode)
        {
            return resource;
        }

        resource.Dispose();
        throw new InvalidOperationException("An existing Playwright resource belongs to a different job or stage and cannot be reused.");
    }

    private async Task EnsureKubernetesApiSuccessAsync(
        HttpResponseMessage response,
        string operation,
        string resourceName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var safeResponseBody = SanitizeKubernetesApiResponse(responseBody);
        var detailedMessage =
            $"Kubernetes API request failed while {operation}. Resource: {resourceName}. " +
            $"Status: {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {safeResponseBody}";
        var publicMessage =
            $"Kubernetes API rejected the Playwright resource while {operation}. Resource: {resourceName}. " +
            "Review the complete operation logs or application logs for the detailed Kubernetes response.";

        logger.LogWarning(
            "Kubernetes API request failed while {Operation}. Resource: {ResourceName}. StatusCode: {StatusCode}. Response: {ResponseBody}",
            operation,
            resourceName,
            (int)response.StatusCode,
            safeResponseBody);

        throw new KubernetesApiRequestException(publicMessage, detailedMessage, response.StatusCode);
    }

    private static string SanitizeKubernetesApiResponse(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "No response body was returned by the Kubernetes API.";
        }

        var sanitized = SensitiveLinePattern.Replace(responseBody, match => $"{match.Groups["key"].Value}=***");
        sanitized = Regex.Replace(sanitized, @"[\r\n\t]+", " ");
        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ").Trim();

        const int maxResponseLength = 2000;
        return sanitized.Length <= maxResponseLength
            ? sanitized
            : sanitized[..maxResponseLength] + "...";
    }

    private async Task DeletePersistentVolumeClaimAsync(HttpClient client, string kubernetesNamespace, string pvcName, CancellationToken cancellationToken)
    {
        if (!playwrightOptions.SharedStateVolumeEnabled)
        {
            return;
        }

        using var response = await client.DeleteAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/persistentvolumeclaims/{Uri.EscapeDataString(pvcName)}",
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureKubernetesApiSuccessAsync(
            response,
            "deleting Playwright shared state PersistentVolumeClaim",
            pvcName,
            cancellationToken);
    }

    private object? BuildContainerResources()
    {
        var requests = new Dictionary<string, string>(StringComparer.Ordinal);
        AddResourceQuantity(
            requests,
            "cpu",
            FirstConfiguredValue(configuration["UpgradeCenter:Playwright:JobCpuRequest"], playwrightOptions.JobCpuRequest));
        AddResourceQuantity(
            requests,
            "memory",
            FirstConfiguredValue(configuration["UpgradeCenter:Playwright:JobMemoryRequest"], playwrightOptions.JobMemoryRequest));

        var limits = new Dictionary<string, string>(StringComparer.Ordinal);
        AddResourceQuantity(
            limits,
            "cpu",
            FirstConfiguredValue(configuration["UpgradeCenter:Playwright:JobCpuLimit"], playwrightOptions.JobCpuLimit));
        AddResourceQuantity(
            limits,
            "memory",
            FirstConfiguredValue(configuration["UpgradeCenter:Playwright:JobMemoryLimit"], playwrightOptions.JobMemoryLimit));

        var resources = new Dictionary<string, object>(StringComparer.Ordinal);
        if (requests.Count > 0)
        {
            resources["requests"] = requests;
        }

        if (limits.Count > 0)
        {
            resources["limits"] = limits;
        }

        return resources.Count == 0 ? null : resources;
    }

    private static void AddResourceQuantity(IDictionary<string, string> resources, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            resources[name] = value.Trim();
        }
    }

    private async Task<PlaywrightScriptResult> MonitorJobAsync(
        HttpClient client,
        string kubernetesNamespace,
        string jobName,
        IReadOnlyDictionary<string, string> labels,
        PlaywrightValidationMode mode,
        string upgradeJobId,
        IReadOnlyList<string> redactionValues,
        DateTimeOffset startedAt,
        string command,
        Action<PlaywrightValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(300, GetJobTimeoutSeconds(mode) + 60));
        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(playwrightOptions.JobPollSeconds, 2, 60));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? podName = null;
        string? lastStateMessage = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = await GetJsonAsync(client, $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs/{Uri.EscapeDataString(jobName)}", cancellationToken);
            var condition = GetJobTerminalCondition(job.RootElement);
            podName ??= await TryResolvePodNameAsync(client, kubernetesNamespace, labels, cancellationToken);
            var podState = podName is null
                ? PodState.Pending("Waiting for Playwright runner pod to be scheduled.")
                : await GetPodStateAsync(client, kubernetesNamespace, podName, cancellationToken);

            lastStateMessage = podState.Message;
            UpdateRunningProgress(podState, mode, progress);

            if (condition == "Complete")
            {
                var logs = podName is null
                    ? string.Empty
                    : await TryReadPodLogsAsync(client, kubernetesNamespace, podName, cancellationToken);
                TryCaptureHtmlReport(upgradeJobId, mode, logs);
                logs = PrepareLogsForDisplay(logs, redactionValues);
                var resultSummary = TryGetUploadedResultSummary(upgradeJobId, mode) ?? TryParseResultSummary(logs);
                var successMessage = resultSummary?.ToMessage($"{ToDisplayName(mode)} completed.", mode)
                    ?? $"{ToDisplayName(mode)} completed successfully.";
                successMessage = AppendExecutionSummaryContext(successMessage, startedAt, IsHtmlReportSaved(upgradeJobId, mode));
                var succeeded = IsSuccessfulResult(mode, resultSummary);
                progress?.Invoke(new PlaywrightValidationProgress("playwright-tests", succeeded ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed, successMessage));
                return CreateResult(succeeded, podState.ExitCode ?? 0, false, startedAt, command, logs, string.Empty, successMessage, kubernetesJobCreated: true);
            }

            if (condition == "Failed" || podState.IsFailed)
            {
                var logs = podName is null
                    ? string.Empty
                    : await TryReadPodLogsAsync(client, kubernetesNamespace, podName, cancellationToken);
                TryCaptureHtmlReport(upgradeJobId, mode, logs);
                logs = PrepareLogsForDisplay(logs, redactionValues);
                var resultSummary = TryGetUploadedResultSummary(upgradeJobId, mode) ?? TryParseResultSummary(logs);
                var message = podState.IsInfrastructureFailure
                    ? $"{ToDisplayName(mode)} could not be completed due to an execution error. Review the stage details for more information."
                    : $"{ToDisplayName(mode)} did not complete successfully. Review the stage details for more information.";
                message = resultSummary?.ToMessage(message, mode) ?? message;
                message = AppendExecutionSummaryContext(message, startedAt, IsHtmlReportSaved(upgradeJobId, mode));
                progress?.Invoke(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Failed, message));
                return CreateResult(false, podState.ExitCode, false, startedAt, command, logs, string.Empty, message, kubernetesJobCreated: true);
            }

            await Task.Delay(pollDelay, cancellationToken);
        }

        var timeoutLogs = podName is null
            ? string.Empty
            : await TryReadPodLogsAsync(client, kubernetesNamespace, podName, CancellationToken.None);
        TryCaptureHtmlReport(upgradeJobId, mode, timeoutLogs);

        var timeoutMessage = $"{ToDisplayName(mode)} timed out. Last status: {lastStateMessage ?? "status was unavailable"}.";
        progress?.Invoke(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Failed, timeoutMessage));
        return CreateResult(false, null, true, startedAt, command, PrepareLogsForDisplay(timeoutLogs, redactionValues), string.Empty, timeoutMessage, kubernetesJobCreated: true);
    }

    private void TryCaptureHtmlReport(string upgradeJobId, PlaywrightValidationMode mode, string logs)
    {
        if (mode == PlaywrightValidationMode.Cleanup)
        {
            return;
        }

        var stage = mode == PlaywrightValidationMode.Pre
            ? "Pre-Upgrade Validation"
            : "Post-Upgrade Validation";

        if (reportStore.Exists(upgradeJobId, mode))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(logs))
        {
            operationLogStore.Append(
                upgradeJobId,
                stage,
                "Warning",
                "PlaywrightReport",
                "Playwright HTML report could not be captured because pod logs were empty or unavailable.");
            return;
        }

        var saved = reportStore.TrySaveFromLogs(upgradeJobId, mode, logs);
        operationLogStore.Append(
            upgradeJobId,
            stage,
            saved ? "Info" : "Warning",
            "PlaywrightReport",
            saved
                ? "Playwright HTML report saved."
                : "Playwright HTML report was not captured. The expected single-report marker was not found in the Playwright pod logs. Verify that the runner generated and uploaded the configured report path before the pod was cleaned up.");
    }

    private bool IsHtmlReportSaved(string upgradeJobId, PlaywrightValidationMode mode)
    {
        return mode != PlaywrightValidationMode.Cleanup && reportStore.Exists(upgradeJobId, mode);
    }

    private static string AppendExecutionSummaryContext(string message, DateTimeOffset startedAt, bool htmlReportSaved)
    {
        var parts = new List<string>
        {
            message.Trim().TrimEnd('.'),
            $"Duration: {FormatDuration(DateTimeOffset.UtcNow - startedAt)}"
        };

        if (htmlReportSaved)
        {
            parts.Add("HTML report saved");
        }

        return string.Join(". ", parts) + ".";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{Math.Max(0, duration.TotalSeconds):0}s"
            : $"{(int)Math.Max(0, duration.TotalMinutes)}m {duration.Seconds}s";
    }

    private static void UpdateRunningProgress(PodState podState, PlaywrightValidationMode mode, Action<PlaywrightValidationProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        if (podState.Phase.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            progress(new PlaywrightValidationProgress("playwright-runner", UpgradeJobStageStatus.Succeeded, "Playwright runner pod is running."));
            progress(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Running, mode == PlaywrightValidationMode.Cleanup
                ? "Running Playwright cleanup tests."
                : "Running Playwright validation tests."));
            return;
        }

        progress(new PlaywrightValidationProgress("playwright-runner", UpgradeJobStageStatus.Running, podState.Message));
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<string?> TryResolvePodNameAsync(
        HttpClient client,
        string kubernetesNamespace,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken)
    {
        var selector = string.Join(
            ",",
            labels
                .Where(label => !string.Equals(label.Key, "app.kubernetes.io/name", StringComparison.OrdinalIgnoreCase))
                .Select(label => $"{label.Key}={label.Value}"));
        using var response = await client.GetAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods?labelSelector={Uri.EscapeDataString(selector)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return items.EnumerateArray()
            .Select(item => TryGetString(item, "metadata", "name"))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private async Task<PodState> GetPodStateAsync(HttpClient client, string kubernetesNamespace, string podName, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(client, $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods/{Uri.EscapeDataString(podName)}", cancellationToken);
        var root = document.RootElement;
        var phase = TryGetString(root, "status", "phase") ?? "Pending";
        var containerStatus = TryGetFirstArrayElement(root, "status", "containerStatuses");
        if (containerStatus.HasValue)
        {
            var state = containerStatus.Value.GetProperty("state");
            if (state.TryGetProperty("terminated", out var terminated))
            {
                var exitCode = TryGetInt(terminated, "exitCode");
                var reason = TryGetString(terminated, "reason") ?? "Terminated";
                var message = TryGetString(terminated, "message") ?? reason;
                return exitCode == 0
                    ? new PodState(phase, false, false, exitCode, "Playwright runner completed successfully.")
                    : new PodState(phase, true, false, exitCode, $"{reason}. {message}".Trim());
            }

            if (state.TryGetProperty("waiting", out var waiting))
            {
                var reason = TryGetString(waiting, "reason") ?? "Waiting";
                var message = TryGetString(waiting, "message") ?? reason;
                var isInfrastructureFailure = reason.Contains("ImagePull", StringComparison.OrdinalIgnoreCase) ||
                                              reason.Contains("ErrImage", StringComparison.OrdinalIgnoreCase) ||
                                              reason.Contains("CreateContainer", StringComparison.OrdinalIgnoreCase);
                return new PodState(phase, isInfrastructureFailure, isInfrastructureFailure, null, $"{reason}. {message}".Trim());
            }
        }

        return PodState.Pending($"Playwright runner pod phase: {phase}.");
    }

    private async Task<string> TryReadPodLogsAsync(
        HttpClient client,
        string kubernetesNamespace,
        string podName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods/{Uri.EscapeDataString(podName)}/log?container=playwright-runner",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"Playwright pod logs could not be read. Status code: {(int)response.StatusCode}.";
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Playwright pod logs could not be read: {exception.Message}";
        }
    }

    private async Task DeleteSecretAsync(HttpClient client, string kubernetesNamespace, string secretName, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/secrets/{Uri.EscapeDataString(secretName)}",
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private KubernetesRuntimeContext ResolveContext()
    {
        var kubernetesNamespace = FirstConfiguredValue(
            kubernetesOptions.Namespace,
            Environment.GetEnvironmentVariable("POD_NAMESPACE"),
            ReadFileIfExists(kubernetesOptions.NamespacePath));

        var apiServer = FirstConfiguredValue(
            kubernetesOptions.ApiServer,
            ResolveInClusterApiServer());

        if (string.IsNullOrWhiteSpace(kubernetesNamespace))
        {
            return KubernetesRuntimeContext.Failed(null, "Kubernetes namespace could not be resolved from configuration, POD_NAMESPACE, or the service account namespace file.");
        }

        if (string.IsNullOrWhiteSpace(apiServer))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, "Kubernetes API server could not be resolved from configuration or in-cluster environment variables.");
        }

        if (!File.Exists(kubernetesOptions.ServiceAccountTokenPath))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, $"Kubernetes service account token was not found at '{kubernetesOptions.ServiceAccountTokenPath}'.");
        }

        var token = File.ReadAllText(kubernetesOptions.ServiceAccountTokenPath).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, "Kubernetes service account token file is empty.");
        }

        return new KubernetesRuntimeContext(true, kubernetesNamespace.Trim(), apiServer.TrimEnd('/'), token, "Kubernetes runtime context resolved.");
    }

    private HttpClient CreateKubernetesHttpClient(KubernetesRuntimeContext context)
    {
        var handler = new HttpClientHandler();
        if (kubernetesOptions.SkipTlsVerify)
        {
            if (!CanSkipTlsVerify())
            {
                logger.LogError("Kubernetes TLS verification bypass was blocked because SkipTlsVerify is enabled without an explicit local development override.");
                throw new InvalidOperationException("Kubernetes TLS verification cannot be disabled unless an explicit local development override is enabled. Configure the service account certificate authority file instead.");
            }

            logger.LogWarning("Kubernetes TLS verification is disabled by explicit local development configuration.");
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (File.Exists(kubernetesOptions.CertificateAuthorityPath))
        {
            var certificateAuthority = new X509Certificate2(kubernetesOptions.CertificateAuthorityPath);
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
        return kubernetesOptions.AllowSkipTlsVerifyForLocalDevelopment &&
            IsLocalDevelopmentEnvironment(hostEnvironment.EnvironmentName);
    }

    private static bool IsLocalDevelopmentEnvironment(string? environmentName)
    {
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Dev", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "LocalDevelopment", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpContent ToJsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static Dictionary<string, string> ReadSecretData(JsonElement secret)
    {
        if (!secret.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            var encodedValue = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(encodedValue))
            {
                continue;
            }

            values[property.Name] = Encoding.UTF8.GetString(Convert.FromBase64String(encodedValue));
        }

        return values;
    }

    private static string? GetJobTerminalCondition(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var condition in conditions.EnumerateArray())
        {
            var type = TryGetString(condition, "type");
            var statusText = TryGetString(condition, "status");
            if (string.Equals(statusText, "True", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(type, "Complete", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "Failed", StringComparison.OrdinalIgnoreCase)))
            {
                return type;
            }
        }

        return null;
    }

    private static JsonElement? TryGetFirstArrayElement(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 0
            ? current[0]
            : null;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is not configured.");
        }

        return value.Trim();
    }

    private static string PrepareLogsForDisplay(string logs, IReadOnlyList<string> redactionValues)
    {
        return SanitizeLogs(RemoveEmbeddedReport(logs), redactionValues);
    }

    private static string RemoveEmbeddedReport(string logs)
    {
        return RemoveMarkedBlock(
            logs,
            PlaywrightReportStore.ReportBeginMarker,
            PlaywrightReportStore.ReportEndMarker);
    }

    private static string RemoveMarkedBlock(string logs, string beginMarker, string endMarker)
    {
        var begin = logs.LastIndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return logs;
        }

        var end = logs.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            return logs[..begin];
        }

        end += endMarker.Length;
        return logs.Remove(begin, end - begin);
    }

    private string BuildReportUploadUrl(string upgradeJobId, PlaywrightValidationMode mode)
    {
        var baseUrl = FirstConfiguredValue(
            playwrightOptions.ReportUploadBaseUrl,
            configuration["UpgradeCenter:Playwright:ReportUploadBaseUrl"],
            "http://bold-upgrade-center/upgrade-center/api/playwright-reports");
        var stage = mode switch
        {
            PlaywrightValidationMode.Post => "post-upgrade",
            PlaywrightValidationMode.Cleanup => "cleanup",
            _ => "pre-upgrade"
        };
        return $"{baseUrl!.TrimEnd('/')}/{Uri.EscapeDataString(upgradeJobId)}/{stage}";
    }

    private string BuildResultUploadUrl(string upgradeJobId, PlaywrightValidationMode mode)
    {
        var baseUrl = FirstConfiguredValue(
            playwrightOptions.ResultUploadBaseUrl,
            configuration["UpgradeCenter:Playwright:ResultUploadBaseUrl"],
            "http://bold-upgrade-center/upgrade-center/api/playwright-results");
        var stage = mode switch
        {
            PlaywrightValidationMode.Post => "post-upgrade",
            PlaywrightValidationMode.Cleanup => "cleanup",
            _ => "pre-upgrade"
        };
        return $"{baseUrl!.TrimEnd('/')}/{Uri.EscapeDataString(upgradeJobId)}/{stage}";
    }

    private static string SanitizeLogs(string logs, IReadOnlyList<string> redactionValues)
    {
        var sanitized = logs;
        foreach (var value in redactionValues)
        {
            sanitized = sanitized.Replace(value, "***", StringComparison.Ordinal);
        }

        sanitized = SensitiveLinePattern.Replace(sanitized, match => $"{match.Groups["key"].Value}=***");
        return sanitized.Length <= 12000 ? sanitized : sanitized[^12000..];
    }

    private static bool IsSuccessfulResult(PlaywrightValidationMode mode, PlaywrightResultSummary? summary)
    {
        if (mode != PlaywrightValidationMode.Cleanup)
        {
            return true;
        }

        return summary is not null &&
               string.Equals(summary.Status, "Passed", StringComparison.OrdinalIgnoreCase) &&
               summary.Total > 0 &&
               summary.Failed == 0 &&
               summary.Passed + summary.Skipped == summary.Total;
    }

    private static PlaywrightResultSummary? TryParseResultSummary(string logs)
    {
        const string marker = "PLAYWRIGHT_RESULT_JSON ";
        var index = logs.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var lineEnd = logs.IndexOfAny(['\r', '\n'], start);
        var json = lineEnd < 0 ? logs[start..] : logs[start..lineEnd];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new PlaywrightResultSummary(
                TryGetInt(root, "total") ?? 0,
                TryGetInt(root, "passed") ?? 0,
                TryGetInt(root, "failed") ?? 0,
                TryGetInt(root, "skipped") ?? 0,
                TryGetDouble(root, "passPercentage") ?? 0,
                TryGetInt(root, "threshold") ?? 100,
                TryGetString(root, "status") ?? "Unknown");
        }
        catch
        {
            return null;
        }
    }

    private PlaywrightResultSummary? TryGetUploadedResultSummary(string upgradeJobId, PlaywrightValidationMode mode)
    {
        return validationResultStore.TryGet(upgradeJobId, mode, out var summary) && summary is not null
            ? new PlaywrightResultSummary(
                summary.Total,
                summary.Passed,
                summary.Failed,
                summary.Skipped,
                summary.PassPercentage,
                summary.Threshold,
                summary.Status)
            : null;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static PlaywrightResourceNames CreatePlaywrightResourceNames(string jobId, string mode)
    {
        var shortJobId = CreateShortJobId(jobId);
        return new PlaywrightResourceNames(
            ShortJobId: shortJobId,
            JobName: CreateKubernetesName($"pw-{mode}", shortJobId),
            SecretName: CreateKubernetesName($"pw-secret-{mode}", shortJobId),
            SharedStatePvcName: GetSharedStatePvcName(jobId));
    }

    private static string GetSharedStatePvcName(string jobId)
    {
        return CreateKubernetesName("pw-state", CreateShortJobId(jobId));
    }

    private static string CreateShortJobId(string jobId)
    {
        var normalized = Regex.Replace((jobId ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "job";
        }

        var prefix = normalized.Length <= 8 ? normalized : normalized[..8];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId ?? string.Empty)))[..8].ToLowerInvariant();
        return $"{prefix}-{hash}";
    }

    private static string CreateKubernetesName(string prefix, string suffix)
    {
        var normalizedPrefix = Regex.Replace((prefix ?? string.Empty).ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        var normalizedSuffix = Regex.Replace((suffix ?? string.Empty).ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        var name = $"{normalizedPrefix}-{normalizedSuffix}".Trim('-');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "pw-job";
        }

        if (name.Length > 63)
        {
            name = name[..63].Trim('-');
        }

        return Regex.Replace(name, "^[^a-z0-9]+|[^a-z0-9]+$", string.Empty);
    }

    private bool ShouldDeleteSharedStatePvc(PlaywrightValidationMode mode, PlaywrightScriptResult? result, bool sharedStatePvcCreated, bool jobCreated)
    {
        if (!playwrightOptions.SharedStateVolumeEnabled)
        {
            return false;
        }

        return mode == PlaywrightValidationMode.Cleanup ||
               (sharedStatePvcCreated && !jobCreated);
    }

    private string GetSharedStateAccessMode()
    {
        return FirstConfiguredValue(
            playwrightOptions.SharedStateAccessMode,
            configuration["UpgradeCenter:Playwright:SharedStateAccessMode"],
            "ReadWriteOnce")!.Trim();
    }

    private string GetSharedStateStorageSize()
    {
        return FirstConfiguredValue(
            playwrightOptions.SharedStateStorageSize,
            configuration["UpgradeCenter:Playwright:SharedStateStorageSize"],
            "1Gi")!.Trim();
    }

    private string GetSharedStateMountPath()
    {
        return FirstConfiguredValue(
            playwrightOptions.SharedStateMountPath,
            configuration["UpgradeCenter:Playwright:SharedStateMountPath"],
            "/app/playwright-shared-state")!.TrimEnd('/');
    }

    private int? GetSharedStateFsGroup()
    {
        var configuredValue = FirstConfiguredValue(
            playwrightOptions.SharedStateFsGroup?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration["UpgradeCenter:Playwright:SharedStateFsGroup"]);

        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return null;
        }

        return int.TryParse(configuredValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var fsGroup) && fsGroup > 0
            ? fsGroup
            : null;
    }

    private static string? ReadFileIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path).Trim();
    }

    private static string? ResolveInClusterApiServer()
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        return string.IsNullOrWhiteSpace(host) ? null : $"https://{host}:{port}";
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static PlaywrightScriptResult CreateResult(
        bool succeeded,
        int? exitCode,
        bool timedOut,
        DateTimeOffset startedAt,
        string command,
        string standardOutput,
        string standardError,
        string message,
        bool kubernetesJobCreated = false)
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
            message,
            kubernetesJobCreated);
    }

    private sealed record RuntimeConfiguration(
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<string> RedactionValues)
    {
        public RuntimeConfiguration WithPlaywrightUploads(string reportUploadUrl, string resultUploadUrl, string token)
        {
            var values = Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_URL"] = reportUploadUrl;
            values["BOLD_UPGRADE_CENTER_RESULT_UPLOAD_URL"] = resultUploadUrl;
            values["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_TOKEN"] = token;

            var redactionValues = RedactionValues
                .Concat(new[] { token })
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new RuntimeConfiguration(values, redactionValues);
        }

        public RuntimeConfiguration WithSharedStateDirectory(string sharedStateDirectory)
        {
            var values = Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values["BOLD_PLAYWRIGHT_SHARED_STATE_DIR"] = sharedStateDirectory;
            return new RuntimeConfiguration(values, RedactionValues);
        }
    }

    private sealed record DatabaseRuntimeValues(
        string? Host,
        string? Port,
        string? DatabaseName,
        string DatabaseType,
        string? Username,
        string? Password,
        DatabaseSslDisposition SslDisposition);

    private enum DatabaseSslDisposition
    {
        Disabled,
        Enabled,
        Ambiguous
    }

    private sealed record PlaywrightResultSummary(
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        double PassPercentage,
        int Threshold,
        string Status)
    {
        public string ToMessage(string prefix, PlaywrightValidationMode mode)
        {
            if (mode == PlaywrightValidationMode.Cleanup)
            {
                return string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) &&
                       Total > 0 &&
                       Failed == 0 &&
                       Passed + Skipped == Total
                    ? "Cleanup completed successfully. All validation resources were removed."
                    : "Cleanup could not be completed. Manual cleanup is required.";
            }

            var itemName = "tests";
            var label = "Validation";
            var skippedText = Skipped > 0 ? $", and {Skipped} were skipped" : string.Empty;
            var failedText = Failed > 0 ? $", {Failed} failed" : string.Empty;

            if (!string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) &&
                Failed == 0 &&
                PassPercentage < Threshold)
            {
                return $"{label} did not meet the required pass threshold. {Passed} of {Total} {itemName} passed ({PassPercentage:0.##}%; required: {Threshold}%){skippedText}.";
            }

            if (!string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) && Failed > 0)
            {
                return $"{label} failed. {Passed} of {Total} {itemName} passed{failedText}{skippedText}. Pass rate: {PassPercentage:0.##}%; required: {Threshold}%.";
            }

            return $"{prefix} {Passed} of {Total} {itemName} passed ({PassPercentage:0.##}%; required: {Threshold}%){failedText}{skippedText}.";
        }
    }

    private sealed record KubernetesRuntimeContext(bool Succeeded, string? Namespace, string? ApiServer, string? Token, string Message)
    {
        public static KubernetesRuntimeContext Failed(string? kubernetesNamespace, string message)
        {
            return new KubernetesRuntimeContext(false, kubernetesNamespace, null, null, message);
        }
    }

    private sealed record PlaywrightResourceNames(
        string ShortJobId,
        string JobName,
        string SecretName,
        string SharedStatePvcName);

    private sealed class KubernetesApiRequestException : HttpRequestException
    {
        public KubernetesApiRequestException(string publicMessage, string detailedMessage, System.Net.HttpStatusCode statusCode)
            : base(publicMessage, null, statusCode)
        {
            PublicMessage = publicMessage;
            DetailedMessage = detailedMessage;
        }

        public string PublicMessage { get; }

        public string DetailedMessage { get; }
    }

    private sealed record PodState(
        string Phase,
        bool IsFailed,
        bool IsInfrastructureFailure,
        int? ExitCode,
        string Message)
    {
        public static PodState Pending(string message)
        {
            return new PodState("Pending", false, false, null, message);
        }
    }
}
