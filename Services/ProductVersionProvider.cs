using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class ProductVersionProvider : IProductVersionProvider
{
    public const string HttpClientName = "UpgradeCenterProductVersion";

    private const string UnknownVersion = "Unavailable";
    private const string ProductVersionApiSource = "Product Version API";
    private const string ConfigurationSource = "IDP configuration";

    private readonly IUpgradeProductContext productContext;
    private readonly IUpgradeCenterConfigurationProvider configurationProvider;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<ProductVersionProvider> logger;

    public ProductVersionProvider(
        IUpgradeProductContext productContext,
        IUpgradeCenterConfigurationProvider configurationProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<ProductVersionProvider> logger)
    {
        this.productContext = productContext;
        this.configurationProvider = configurationProvider;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    public async Task<ProductVersionInfo> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        var product = productContext.Current;
        var runtimeConfiguration = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var apiVersion = await TryGetProductVersionFromApiAsync(product, runtimeConfiguration, cancellationToken);
        if (!string.IsNullOrWhiteSpace(apiVersion))
        {
            return new ProductVersionInfo(product.DisplayName, apiVersion, $"{product.DisplayName} {ProductVersionApiSource}");
        }

        var configuredVersion = product.Product == UpgradeProduct.BoldReports
            ? runtimeConfiguration.BoldReportsVersion
            : runtimeConfiguration.BoldBiVersion;
        if (string.IsNullOrWhiteSpace(configuredVersion))
        {
            logger.LogWarning("{ProductName} version was not available from the Product Version API or the IDP Configuration API.", product.DisplayName);
            return new ProductVersionInfo(product.DisplayName, UnknownVersion);
        }

        return new ProductVersionInfo(product.DisplayName, configuredVersion.Trim(), ConfigurationSource);
    }

    private async Task<string?> TryGetProductVersionFromApiAsync(
        UpgradeProductDefinition product,
        UpgradeCenterRuntimeConfiguration runtimeConfiguration,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveProductVersionEndpoint(product, runtimeConfiguration);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger.LogWarning("{ProductName} Product Version API endpoint could not be resolved. Falling back to configuration-based version retrieval.", product.DisplayName);
            return null;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "{ProductName} Product Version API request failed. StatusCode: {StatusCode}. Endpoint: {Endpoint}. Falling back to configuration-based version retrieval.",
                    product.DisplayName,
                    (int)response.StatusCode,
                    endpoint);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!IsSuccessfulApiResponse(document.RootElement))
            {
                logger.LogWarning("{ProductName} Product Version API returned an unsuccessful response. Falling back to configuration-based version retrieval.", product.DisplayName);
                return null;
            }

            var version = GetStringProperty(document.RootElement, "Data")?.Trim();
            if (string.IsNullOrWhiteSpace(version) || !ProductVersionComparer.TryParse(version, out _))
            {
                logger.LogWarning("{ProductName} Product Version API returned an invalid product version. Falling back to configuration-based version retrieval.", product.DisplayName);
                return null;
            }

            return version;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to retrieve {ProductName} version from the Product Version API. Falling back to configuration-based version retrieval.", product.DisplayName);
            return null;
        }
    }

    private static string? ResolveProductVersionEndpoint(
        UpgradeProductDefinition product,
        UpgradeCenterRuntimeConfiguration runtimeConfiguration)
    {
        if (!string.IsNullOrWhiteSpace(runtimeConfiguration.IdpBaseUrl))
        {
            return CombineUrl(runtimeConfiguration.IdpBaseUrl, product.ProductVersionPath);
        }

        var productBaseUrl = product.Product == UpgradeProduct.BoldReports
            ? runtimeConfiguration.ReportsBaseUrl
            : runtimeConfiguration.BiBaseUrl;
        if (!string.IsNullOrWhiteSpace(productBaseUrl))
        {
            return CombineUrl(productBaseUrl, "api/product-version");
        }

        return null;
    }

    private static bool IsSuccessfulApiResponse(JsonElement root)
    {
        return IsTrueOrMissing(root, "ApiStatus") && IsTrueOrMissing(root, "Status");
    }

    private static bool IsTrueOrMissing(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out var boolValue))
        {
            return boolValue;
        }

        return false;
    }

    private static string? GetStringProperty(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }
}
