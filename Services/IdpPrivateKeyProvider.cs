using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public interface IIdpPrivateKeyProvider
{
    Task<string> GetEncryptedPrivateKeyAsync(CancellationToken cancellationToken = default);
}

public sealed class IdpPrivateKeyProvider : IIdpPrivateKeyProvider
{
    public const string HttpClientName = "UpgradeCenterIdpPrivateKey";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly IOptions<UpgradeCenterOptions> options;
    private readonly ILogger<IdpPrivateKeyProvider> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? cachedEncryptedPrivateKey;
    private string? lastKnownGoodEncryptedPrivateKey;
    private DateTimeOffset cacheExpiresAt;

    public IdpPrivateKeyProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<UpgradeCenterOptions> options,
        ILogger<IdpPrivateKeyProvider> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.options = options;
        this.logger = logger;
    }

    public async Task<string> GetEncryptedPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(cachedEncryptedPrivateKey) &&
            cacheExpiresAt > DateTimeOffset.UtcNow)
        {
            return cachedEncryptedPrivateKey;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(cachedEncryptedPrivateKey) &&
                cacheExpiresAt > DateTimeOffset.UtcNow)
            {
                return cachedEncryptedPrivateKey;
            }

            cachedEncryptedPrivateKey = await LoadEncryptedPrivateKeyAsync(cancellationToken);
            cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
            return cachedEncryptedPrivateKey;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<string> LoadEncryptedPrivateKeyAsync(CancellationToken cancellationToken)
    {
        var idpApiUrl = ResolveIdpApiUrl();
        if (string.IsNullOrWhiteSpace(idpApiUrl))
        {
            return UseLastKnownGoodOrThrow("IDP API internal URL is not configured.");
        }

        try
        {
            var requestUri = CombineUrl(idpApiUrl, "configuration/private-key");
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "IDP private key API request failed. StatusCode: {StatusCode}. Endpoint: {Endpoint}.",
                    (int)response.StatusCode,
                    requestUri);
                return UseLastKnownGoodOrThrow("IDP private key API request failed.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!TryExtractEncryptedPrivateKey(document.RootElement, out var encryptedPrivateKey) ||
                string.IsNullOrWhiteSpace(encryptedPrivateKey))
            {
                logger.LogWarning("IDP private key API response did not contain private key content.");
                return UseLastKnownGoodOrThrow("IDP private key API did not return private key content.");
            }

            lastKnownGoodEncryptedPrivateKey = encryptedPrivateKey;
            logger.LogInformation("Encrypted private key metadata was retrieved from IDP internal API.");
            return encryptedPrivateKey;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "IDP private key API request timed out.");
            return UseLastKnownGoodOrThrow("IDP private key API request timed out.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to retrieve encrypted private key metadata from IDP internal API.");
            return UseLastKnownGoodOrThrow("Unable to retrieve encrypted private key metadata from IDP internal API.");
        }
    }

    private string UseLastKnownGoodOrThrow(string reason)
    {
        if (!string.IsNullOrWhiteSpace(lastKnownGoodEncryptedPrivateKey))
        {
            logger.LogWarning("Using last-known-good encrypted private key metadata. Reason: {Reason}", reason);
            return lastKnownGoodEncryptedPrivateKey;
        }

        logger.LogWarning("No last-known-good encrypted private key metadata is available. Reason: {Reason}", reason);
        throw new InvalidOperationException(reason);
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

    private static bool TryExtractEncryptedPrivateKey(JsonElement root, out string? encryptedPrivateKey)
    {
        encryptedPrivateKey = null;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.String)
            {
                using var dataDocument = JsonDocument.Parse(data.GetString() ?? "{}");
                return TryExtractEncryptedPrivateKey(dataDocument.RootElement, out encryptedPrivateKey);
            }

            if (data.ValueKind == JsonValueKind.Object &&
                TryGetStringProperty(data, out encryptedPrivateKey, "encryptedPrivateKey", "encrypted_private_key"))
            {
                return true;
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            TryGetStringProperty(root, out encryptedPrivateKey, "encryptedPrivateKey", "encrypted_private_key"))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetStringProperty(JsonElement element, out string? value, params string[] propertyNames)
    {
        value = null;
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
        }

        return false;
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
