using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class IdpConfigurationProvider : IUpgradeCenterConfigurationProvider
{
    public const string HttpClientName = "UpgradeCenterIdpConfiguration";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly IOptions<UpgradeCenterOptions> options;
    private readonly ILogger<IdpConfigurationProvider> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private UpgradeCenterRuntimeConfiguration? cachedConfiguration;
    private UpgradeCenterRuntimeConfiguration? lastKnownGoodConfiguration;
    private DateTimeOffset cacheExpiresAt;

    public IdpConfigurationProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<UpgradeCenterOptions> options,
        ILogger<IdpConfigurationProvider> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.options = options;
        this.logger = logger;
    }

    public async Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (cachedConfiguration is not null && cacheExpiresAt > DateTimeOffset.UtcNow)
        {
            return cachedConfiguration;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedConfiguration is not null && cacheExpiresAt > DateTimeOffset.UtcNow)
            {
                return cachedConfiguration;
            }

            cachedConfiguration = await LoadConfigurationAsync(cancellationToken);
            cacheExpiresAt = DateTimeOffset.UtcNow.Add(
                HasMasterDatabaseConfiguration(cachedConfiguration)
                    ? CacheDuration
                    : TimeSpan.FromSeconds(15));
            return cachedConfiguration;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<UpgradeCenterRuntimeConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var idpApiUrl = ResolveIdpApiUrl();
        if (string.IsNullOrWhiteSpace(idpApiUrl))
        {
            logger.LogWarning("IDP Configuration API URL is not configured. Upgrade Center authentication will use explicit fallback values only.");
            return UseLastKnownGoodOrFallback("IDP Configuration API URL is not configured.");
        }

        try
        {
            var requestUri = CombineUrl(idpApiUrl, "configuration");
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "IDP Configuration API request failed. StatusCode: {StatusCode}.",
                    (int)response.StatusCode);
                return UseLastKnownGoodOrFallback("IDP Configuration API request failed.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var configurationJson = ExtractConfigurationJson(document.RootElement);
            if (string.IsNullOrWhiteSpace(configurationJson))
            {
                logger.LogWarning("IDP Configuration API response did not contain configuration data.");
                return UseLastKnownGoodOrFallback("IDP Configuration API response did not contain configuration data.");
            }

            using var configurationDocument = JsonDocument.Parse(configurationJson);
            var root = configurationDocument.RootElement;
            var idpUrl = GetProperty(root, "InternalAppUrls", "Idp");
            var biUrl = GetProperty(root, "InternalAppUrls", "Bi");
            var reportsUrl = FirstConfiguredValue(
                GetProperty(root, "InternalAppUrls", "Reports"),
                GetProperty(root, "InternalAppUrls", "Report"),
                GetProperty(root, "InternalAppUrls", "Reporting"));
            var version = GetBoldProductVersion(root, "BoldBI");
            var reportsVersion = GetBoldProductVersion(root, "BoldReports");
            var encryptedMasterConnectionString = GetProperty(root, "SqlConfiguration", "ConnectionString");
            var masterDatabaseServerType = GetIntegerProperty(root, "SqlConfiguration", "ServerType");
            var machineKeyDecryptionKey = GetProperty(root, "MachineKey", "DecryptionKey");
            var internalAppClientId = GetRootProperty(root, "InternalAppClientId");
            var internalAppClientSecret = GetRootProperty(root, "InternalAppClientSecret");
            var masterDatabaseSchemaName = GetProperty(root, "SqlConfiguration", "SchemaName");
            var masterDatabaseTablePrefix = GetProperty(root, "SqlConfiguration", "Prefix");
            var useSingleTenantDb = GetBooleanRootProperty(root, "UseSingleTenantDb") ??
                                    GetBooleanProperty(root, "SystemSettings", "UseSingleTenantDb");
            var showDataHub = GetBooleanRootProperty(root, "ShowDataHub");
            if (!showDataHub.HasValue && HasProperty(root, "ShowDataHub"))
            {
                logger.LogWarning("IDP configuration contains ShowDataHub, but the value is not a valid boolean. The Playwright runner will use its safe default.");
            }

            var fallback = CreateFallbackConfiguration();
            var runtimeConfiguration = new UpgradeCenterRuntimeConfiguration(
                FirstConfiguredValue(idpUrl, fallback.IdpBaseUrl)?.TrimEnd('/'),
                FirstConfiguredValue(biUrl, fallback.BiBaseUrl)?.TrimEnd('/'),
                FirstConfiguredValue(version, fallback.BoldBiVersion),
                encryptedMasterConnectionString,
                masterDatabaseServerType,
                machineKeyDecryptionKey,
                internalAppClientId,
                internalAppClientSecret,
                useSingleTenantDb,
                FirstConfiguredValue(masterDatabaseSchemaName, fallback.MasterDatabaseSchemaName),
                FirstConfiguredValue(masterDatabaseTablePrefix, fallback.MasterDatabaseTablePrefix),
                showDataHub,
                FirstConfiguredValue(reportsUrl, fallback.ReportsBaseUrl)?.TrimEnd('/'),
                FirstConfiguredValue(reportsVersion, fallback.BoldReportsVersion));

            if (!IsValidRuntimeConfiguration(runtimeConfiguration))
            {
                logger.LogWarning("IDP Configuration API returned a configuration response, but none of the required Upgrade Center values were available.");
                return UseLastKnownGoodOrFallback("IDP Configuration API returned incomplete configuration data.");
            }

            lastKnownGoodConfiguration = runtimeConfiguration;
            logger.LogInformation(
                "Loaded Upgrade Center runtime configuration from IDP Configuration API. HasIdpUrl: {HasIdpUrl}. HasBiUrl: {HasBiUrl}. HasBoldBiVersion: {HasBoldBiVersion}. HasMasterDatabaseConfiguration: {HasMasterDatabaseConfiguration}. HasInternalAppCredentials: {HasInternalAppCredentials}. HasTenantDatabaseMode: {HasTenantDatabaseMode}. HasShowDataHub: {HasShowDataHub}.",
                !string.IsNullOrWhiteSpace(runtimeConfiguration.IdpBaseUrl),
                !string.IsNullOrWhiteSpace(runtimeConfiguration.BiBaseUrl),
                !string.IsNullOrWhiteSpace(runtimeConfiguration.BoldBiVersion),
                !string.IsNullOrWhiteSpace(encryptedMasterConnectionString) && masterDatabaseServerType.HasValue && !string.IsNullOrWhiteSpace(machineKeyDecryptionKey),
                !string.IsNullOrWhiteSpace(internalAppClientId) && !string.IsNullOrWhiteSpace(internalAppClientSecret),
                runtimeConfiguration.UseSingleTenantDb.HasValue,
                runtimeConfiguration.ShowDataHub.HasValue);
            return runtimeConfiguration;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load Upgrade Center runtime configuration from IDP Configuration API.");
            return UseLastKnownGoodOrFallback("Unable to load Upgrade Center runtime configuration from IDP Configuration API.");
        }
    }

    private UpgradeCenterRuntimeConfiguration UseLastKnownGoodOrFallback(string reason)
    {
        if (lastKnownGoodConfiguration is not null)
        {
            logger.LogWarning("Using last-known-good Upgrade Center runtime configuration. Reason: {Reason}", reason);
            return lastKnownGoodConfiguration;
        }

        logger.LogWarning("Using explicit fallback Upgrade Center runtime configuration. Reason: {Reason}", reason);
        return CreateFallbackConfiguration();
    }

    private static bool IsValidRuntimeConfiguration(UpgradeCenterRuntimeConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.IdpBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.BiBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.ReportsBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.BoldBiVersion) ||
               !string.IsNullOrWhiteSpace(configuration.BoldReportsVersion) ||
               HasMasterDatabaseConfiguration(configuration);
    }

    private static bool HasMasterDatabaseConfiguration(UpgradeCenterRuntimeConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.EncryptedMasterDatabaseConnectionString) &&
               configuration.MasterDatabaseServerType.HasValue &&
               !string.IsNullOrWhiteSpace(configuration.MachineKeyDecryptionKey);
    }

    private string? ResolveIdpApiUrl()
    {
        return FirstConfiguredValue(
            options.Value.Services.IdpApi,
            configuration["UpgradeCenter:Services:IdpApi"],
            configuration["UPGRADE_CENTER_IDP_API_INTERNAL_URL"],
            configuration["IDP_API_INTERNAL_URL"],
            configuration["ID_API_SERVICE_URL"])?.TrimEnd('/');
    }

    private UpgradeCenterRuntimeConfiguration CreateFallbackConfiguration()
    {
        var idpUrl = FirstConfiguredValue(
            options.Value.Authentication.IdpPublicUrlOverride,
            configuration["UpgradeCenter:Authentication:IdpPublicUrlOverride"],
            configuration["UpgradeCenter:Authentication:IdpUrl"],
            configuration["UPGRADE_CENTER_IDP_PUBLIC_URL"],
            configuration["BOLD_UPGRADE_CENTER_IDP_URL"])?.TrimEnd('/');

        return new UpgradeCenterRuntimeConfiguration(idpUrl, null, null);
    }

    private static string? ExtractConfigurationJson(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object &&
                TryGetProperty(data, "config", out var config))
            {
                return config.ValueKind == JsonValueKind.String ? config.GetString() : config.GetRawText();
            }

            return data.ValueKind == JsonValueKind.String ? data.GetString() : data.GetRawText();
        }

        return root.GetRawText();
    }

    private static string? GetProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string? GetRootProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static int? GetIntegerProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String ? MapDatabaseTypeName(value.GetString()) : null;
    }

    private static bool? GetBooleanRootProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return ReadBooleanValue(value);
    }

    private static bool? GetBooleanProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        return ReadBooleanValue(value);
    }

    private static bool? ReadBooleanValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String &&
               bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static int? MapDatabaseTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        return normalized switch
        {
            "mssql" or "sqlserver" or "microsoftsqlserver" => 0,
            "mysql" or "mariadb" => 1,
            "mssqlce" => 2,
            "oracle" or "ora" => 3,
            "postgresql" or "postgres" or "pgsql" => 4,
            _ => null
        };
    }

    private static string? GetBoldProductVersion(JsonElement root, string productName)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "BoldProducts", out var boldProducts))
        {
            return null;
        }

        if (TryReadBoldProductVersion(boldProducts, productName, out var version))
        {
            return version;
        }

        if (boldProducts.ValueKind == JsonValueKind.Object &&
            TryGetProperty(boldProducts, "BoldProduct", out var boldProduct) &&
            TryReadBoldProductVersion(boldProduct, productName, out version))
        {
            return version;
        }

        return null;
    }

    private static bool TryReadBoldProductVersion(JsonElement element, string productName, out string? version)
    {
        version = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            version = IsBoldProduct(element, productName) && TryGetProperty(element, "Version", out var versionElement)
                ? versionElement.GetString()
                : null;
            return !string.IsNullOrWhiteSpace(version);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (IsBoldProduct(item, productName) && TryGetProperty(item, "Version", out var versionElement))
                {
                    version = versionElement.GetString();
                    return !string.IsNullOrWhiteSpace(version);
                }
            }
        }

        return false;
    }

    private static bool IsBoldProduct(JsonElement element, string productName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               TryGetProperty(element, "Name", out var nameElement) &&
               string.Equals(nameElement.GetString(), productName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out _);
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
