using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public enum UpgradeDeploymentMode
{
    Auto,
    BoldBi,
    BoldReports,
    Common
}

public sealed record UpgradeDeploymentModeResult(
    UpgradeDeploymentMode Mode,
    string Source,
    IReadOnlyList<UpgradeProductDefinition> AvailableProducts,
    string? WarningMessage = null);

public interface IUpgradeDeploymentModeService
{
    Task<UpgradeDeploymentModeResult> ResolveAsync(CancellationToken cancellationToken = default);
}

public sealed class UpgradeDeploymentModeService : IUpgradeDeploymentModeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlySet<string> BoldBiCoreDeployments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bi-web-deployment",
        "bi-api-deployment",
        "bi-jobs-deployment",
        "bi-dataservice-deployment"
    };

    private static readonly IReadOnlySet<string> BoldReportsCoreDeployments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "reports-web-deployment",
        "reports-api-deployment",
        "reports-jobs-deployment",
        "reports-reportservice-deployment",
        "reports-viewer-deployment"
    };

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private readonly IOptionsMonitor<UpgradeCenterOptions> upgradeCenterOptions;
    private readonly IOptionsMonitor<KubernetesUpgradeOptions> kubernetesOptions;
    private readonly IHostEnvironment hostEnvironment;
    private readonly ILogger<UpgradeDeploymentModeService> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private UpgradeDeploymentModeResult? cachedAutoResult;
    private DateTimeOffset cacheExpiresAt;

    public UpgradeDeploymentModeService(
        IOptionsMonitor<UpgradeCenterOptions> upgradeCenterOptions,
        IOptionsMonitor<KubernetesUpgradeOptions> kubernetesOptions,
        IHostEnvironment hostEnvironment,
        ILogger<UpgradeDeploymentModeService> logger)
    {
        this.upgradeCenterOptions = upgradeCenterOptions;
        this.kubernetesOptions = kubernetesOptions;
        this.hostEnvironment = hostEnvironment;
        this.logger = logger;
    }

    public async Task<UpgradeDeploymentModeResult> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var configuredMode = ParseDeploymentMode(upgradeCenterOptions.CurrentValue.DeploymentMode);
        if (configuredMode != UpgradeDeploymentMode.Auto)
        {
            await ValidateConfiguredModeAsync(configuredMode, cancellationToken);
            return CreateResult(configuredMode, "configuration");
        }

        return await ResolveAutoAsync(cancellationToken);
    }

    private async Task<UpgradeDeploymentModeResult> ResolveAutoAsync(CancellationToken cancellationToken)
    {
        if (cachedAutoResult is not null && cacheExpiresAt > DateTimeOffset.UtcNow)
        {
            return cachedAutoResult;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedAutoResult is not null && cacheExpiresAt > DateTimeOffset.UtcNow)
            {
                return cachedAutoResult;
            }

            cachedAutoResult = await DetectModeAsync(cancellationToken);
            cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
            return cachedAutoResult;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task ValidateConfiguredModeAsync(UpgradeDeploymentMode configuredMode, CancellationToken cancellationToken)
    {
        try
        {
            var detected = await DetectModeFromKubernetesAsync(cancellationToken);
            if (detected.HasValue &&
                detected.Value != UpgradeDeploymentMode.Common &&
                configuredMode != UpgradeDeploymentMode.Common &&
                detected.Value != configuredMode)
            {
                logger.LogWarning(
                    "Configured Upgrade Center deployment mode differs from Kubernetes auto-detection. ConfiguredMode: {ConfiguredMode}. DetectedMode: {DetectedMode}. The configured mode will be used.",
                    configuredMode,
                    detected.Value);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to validate configured Upgrade Center deployment mode using Kubernetes auto-detection.");
        }
    }

    private async Task<UpgradeDeploymentModeResult> DetectModeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var detectedMode = await DetectModeFromKubernetesAsync(cancellationToken);
            if (detectedMode.HasValue)
            {
                logger.LogInformation("Upgrade Center deployment mode auto-detected from Kubernetes deployments. Mode: {Mode}.", detectedMode.Value);
                return CreateResult(detectedMode.Value, "auto");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to auto-detect Upgrade Center deployment mode from Kubernetes deployments.");
        }

        const string warning = "Upgrade Center deployment mode could not be auto-detected. Showing all supported products.";
        logger.LogWarning(warning);
        return CreateResult(UpgradeDeploymentMode.Common, "fallback", warning);
    }

    private async Task<UpgradeDeploymentMode?> DetectModeFromKubernetesAsync(CancellationToken cancellationToken)
    {
        var context = ResolveContext();
        if (!context.Succeeded)
        {
            logger.LogDebug("Upgrade Center deployment mode auto-detection skipped. Reason: {Reason}", context.Message);
            return null;
        }

        using var client = CreateKubernetesHttpClient(context);
        var deploymentNames = await ListDeploymentNamesAsync(client, context.Namespace!, cancellationToken);
        var hasBoldBi = deploymentNames.Any(BoldBiCoreDeployments.Contains);
        var hasBoldReports = deploymentNames.Any(BoldReportsCoreDeployments.Contains);

        return (hasBoldBi, hasBoldReports) switch
        {
            (true, true) => UpgradeDeploymentMode.Common,
            (true, false) => UpgradeDeploymentMode.BoldBi,
            (false, true) => UpgradeDeploymentMode.BoldReports,
            _ => null
        };
    }

    private static UpgradeDeploymentModeResult CreateResult(UpgradeDeploymentMode mode, string source, string? warningMessage = null)
    {
        return mode switch
        {
            UpgradeDeploymentMode.BoldReports => new(mode, source, [UpgradeProductDefinitions.BoldReports], warningMessage),
            UpgradeDeploymentMode.BoldBi => new(mode, source, [UpgradeProductDefinitions.BoldBi], warningMessage),
            _ => new(mode, source, UpgradeProductDefinitions.All, warningMessage)
        };
    }

    private static UpgradeDeploymentMode ParseDeploymentMode(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return UpgradeDeploymentMode.Auto;
        }

        return normalized.ToLowerInvariant() switch
        {
            "boldbi" or "bi" or "bold-bi" => UpgradeDeploymentMode.BoldBi,
            "boldreports" or "reports" or "bold-reports" => UpgradeDeploymentMode.BoldReports,
            "common" or "combined" or "both" => UpgradeDeploymentMode.Common,
            "auto" => UpgradeDeploymentMode.Auto,
            _ => UpgradeDeploymentMode.Auto
        };
    }

    private KubernetesRuntimeContext ResolveContext()
    {
        var options = kubernetesOptions.CurrentValue;
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
        var options = kubernetesOptions.CurrentValue;
        var handler = new HttpClientHandler();
        if (options.SkipTlsVerify)
        {
            if (!CanSkipTlsVerify(options))
            {
                throw new InvalidOperationException("Kubernetes TLS verification cannot be disabled unless an explicit local development override is enabled. Configure the service account certificate authority file instead.");
            }

            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (File.Exists(options.CertificateAuthorityPath))
        {
            var certificateAuthority = X509CertificateLoader.LoadCertificateFromFile(options.CertificateAuthorityPath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
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

    private bool CanSkipTlsVerify(KubernetesUpgradeOptions options)
    {
        return options.AllowSkipTlsVerifyForLocalDevelopment &&
            IsLocalDevelopmentEnvironment(hostEnvironment.EnvironmentName);
    }

    private static async Task<IReadOnlyList<string>> ListDeploymentNamesAsync(HttpClient client, string kubernetesNamespace, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/apis/apps/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/deployments", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var deploymentList = await JsonSerializer.DeserializeAsync<KubernetesDeploymentList>(stream, JsonOptions, cancellationToken);
        return (deploymentList?.Items ?? [])
            .Select(item => item.Metadata?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLocalDevelopmentEnvironment(string? environmentName)
    {
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Dev", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "LocalDevelopment", StringComparison.OrdinalIgnoreCase);
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

    private sealed class KubernetesDeploymentList
    {
        public List<KubernetesDeploymentItem>? Items { get; set; }
    }

    private sealed class KubernetesDeploymentItem
    {
        public KubernetesMetadata? Metadata { get; set; }
    }

    private sealed class KubernetesMetadata
    {
        public string? Name { get; set; }
    }
}
