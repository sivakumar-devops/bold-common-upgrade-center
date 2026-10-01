using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class ReleasePlaywrightRunnerImageProvider : IPlaywrightRunnerImageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly UpgradeCenterOptions options;
    private readonly IUpgradeProductContext productContext;
    private readonly ILogger<ReleasePlaywrightRunnerImageProvider> logger;

    public ReleasePlaywrightRunnerImageProvider(
        HttpClient httpClient,
        IOptions<UpgradeCenterOptions> options,
        IUpgradeProductContext productContext,
        ILogger<ReleasePlaywrightRunnerImageProvider> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.productContext = productContext;
        this.logger = logger;
    }

    public async Task<string?> GetRunnerImageAsync(string selectedVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return null;
        }

        var product = productContext.Current;
        var normalizedVersion = NormalizeVersion(selectedVersion);
        var exactRunnerImage = await TryGetRunnerImageAsync(product, normalizedVersion, cancellationToken);
        if (!string.IsNullOrWhiteSpace(exactRunnerImage))
        {
            return exactRunnerImage;
        }

        if (!TryParseVersionAnchor(normalizedVersion, out var requestedVersion))
        {
            logger.LogWarning(
                "Playwright runner image fallback skipped because version could not be parsed. Product: {ProductName}. Version: {Version}.",
                product.DisplayName,
                selectedVersion);
            return null;
        }

        var fallbackVersions = await GetSameFamilyFallbackVersionsAsync(product, requestedVersion, normalizedVersion, cancellationToken);
        foreach (var fallbackVersion in fallbackVersions)
        {
            var fallbackRunnerImage = await TryGetRunnerImageAsync(product, fallbackVersion, cancellationToken);
            if (string.IsNullOrWhiteSpace(fallbackRunnerImage))
            {
                continue;
            }

            logger.LogInformation(
                "Playwright runner image resolved using same release-family fallback. Product: {ProductName}. RequestedVersion: {RequestedVersion}. FallbackVersion: {FallbackVersion}.",
                product.DisplayName,
                normalizedVersion,
                fallbackVersion);
            return fallbackRunnerImage;
        }

        logger.LogWarning(
            "Playwright runner image was not available for exact version or same release-family fallback. Product: {ProductName}. Version: {Version}. ReleaseFamily: {Major}.{Minor}.",
            product.DisplayName,
            normalizedVersion,
            requestedVersion.Major,
            requestedVersion.Minor);
        return null;
    }

    private async Task<string?> TryGetRunnerImageAsync(
        UpgradeProductDefinition product,
        string selectedVersion,
        CancellationToken cancellationToken)
    {
        var uri = BuildVersionApiUri(selectedVersion, "k8s");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Bold-Upgrade-Center/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Playwright runner image metadata request failed. Product: {ProductName}. StatusCode: {StatusCode}. Version: {Version}. Url: {Url}.",
                    product.DisplayName,
                    (int)response.StatusCode,
                    selectedVersion,
                    uri);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var metadata = await JsonSerializer.DeserializeAsync<K8sReleaseMetadata>(stream, JsonOptions, cancellationToken);
            var runnerImage = metadata?.PlaywrightRunnerImage?.Trim();
            if (string.IsNullOrWhiteSpace(runnerImage))
            {
                logger.LogWarning(
                    "Playwright runner image metadata is missing. Product: {ProductName}. Version: {Version}. Url: {Url}.",
                    product.DisplayName,
                    selectedVersion,
                    uri);
                return null;
            }

            if (!IsValidRunnerImageReference(runnerImage, out var validationMessage))
            {
                logger.LogWarning(
                    "Playwright runner image metadata is invalid. Product: {ProductName}. Version: {Version}. Url: {Url}. Reason: {Reason}.",
                    product.DisplayName,
                    selectedVersion,
                    uri,
                    validationMessage);
                return null;
            }

            return runnerImage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to resolve Playwright runner image from release metadata. Product: {ProductName}. Version: {Version}. Url: {Url}.",
                product.DisplayName,
                selectedVersion,
                uri);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> GetSameFamilyFallbackVersionsAsync(
        UpgradeProductDefinition product,
        VersionAnchor requestedVersion,
        string originalVersion,
        CancellationToken cancellationToken)
    {
        var releaseVersionsApiUrl = ResolveReleaseVersionsApiUrl(product);
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseVersionsApiUrl);
        request.Headers.UserAgent.ParseAdd("Bold-Upgrade-Center/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Playwright runner image fallback version-list request failed. Product: {ProductName}. StatusCode: {StatusCode}. Url: {Url}.",
                    product.DisplayName,
                    (int)response.StatusCode,
                    releaseVersionsApiUrl);
                return Array.Empty<string>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var releaseResponse = await JsonSerializer.DeserializeAsync<ReleaseVersionListResponse>(stream, JsonOptions, cancellationToken)
                ?? new ReleaseVersionListResponse();

            var sameFamilyVersions = (releaseResponse.Versions ?? [])
                .Select(version => NormalizeVersion(version.Version ?? string.Empty))
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Where(version => !version.Equals(originalVersion, StringComparison.OrdinalIgnoreCase))
                .Select(version => new
                {
                    Version = version,
                    Parsed = TryParseVersionAnchor(version, out var parsed) ? parsed : null
                })
                .Where(version => version.Parsed is not null &&
                    version.Parsed.Major == requestedVersion.Major &&
                    version.Parsed.Minor == requestedVersion.Minor)
                .GroupBy(version => version.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var sameOrLowerPatch = sameFamilyVersions
                .Where(version => version.Parsed!.Patch <= requestedVersion.Patch)
                .OrderByDescending(version => version.Parsed!.Patch)
                .Select(version => version.Version);

            var higherPatch = sameFamilyVersions
                .Where(version => version.Parsed!.Patch > requestedVersion.Patch)
                .OrderBy(version => version.Parsed!.Patch)
                .Select(version => version.Version);

            return sameOrLowerPatch.Concat(higherPatch).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to resolve same release-family Playwright runner image fallback versions. Product: {ProductName}. Version: {Version}. Url: {Url}.",
                product.DisplayName,
                originalVersion,
                releaseVersionsApiUrl);
            return Array.Empty<string>();
        }
    }

    private static bool IsValidRunnerImageReference(string imageReference, out string message)
    {
        if (!ContainerImageRegistryValidator.TryParseImageReference(imageReference, out var registry, out var repository, out var tag))
        {
            message = "Image reference must include registry, repository, image name, and tag.";
            return false;
        }

        if (!ContainerImageRegistryValidator.TryValidateRegistryAuthorityFormat(registry, out message))
        {
            return false;
        }

        if (repository.Contains(' ') || repository.StartsWith('/') || repository.EndsWith('/'))
        {
            message = "Image repository path is malformed.";
            return false;
        }

        if (tag.Contains(' ') || tag.Length > 128)
        {
            message = "Image tag is malformed.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private string BuildVersionApiUri(string selectedVersion, params string[] segments)
    {
        var product = productContext.Current;
        var baseUri = ResolveReleaseVersionsApiUrl(product).TrimEnd('/');
        var path = string.Join(
            "/",
            new[] { selectedVersion }.Concat(segments).Select(Uri.EscapeDataString));

        return $"{baseUri}/{path}";
    }

    private string ResolveReleaseVersionsApiUrl(UpgradeProductDefinition product)
    {
        if (product.Product == UpgradeProduct.BoldBi &&
            !string.IsNullOrWhiteSpace(options.BiReleaseVersionsApiUrl))
        {
            return options.BiReleaseVersionsApiUrl;
        }

        if (product.Product == UpgradeProduct.BoldBi &&
            !string.IsNullOrWhiteSpace(options.ReleaseVersionsApiUrl))
        {
            return options.ReleaseVersionsApiUrl;
        }

        if (product.Product == UpgradeProduct.BoldReports &&
            !string.IsNullOrWhiteSpace(options.ReportsReleaseVersionsApiUrl))
        {
            return options.ReportsReleaseVersionsApiUrl;
        }

        return product.ReleaseVersionsApiUrl;
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed[1..]
            : trimmed;
    }

    private static bool TryParseVersionAnchor(string version, out VersionAnchor anchor)
    {
        anchor = default!;
        var normalized = NormalizeVersion(version);
        var parts = normalized.Split('.', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !int.TryParse(ReadLeadingDigits(parts[0]), out var major) ||
            !int.TryParse(ReadLeadingDigits(parts[1]), out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length >= 3)
        {
            _ = int.TryParse(ReadLeadingDigits(parts[2]), out patch);
        }

        anchor = new VersionAnchor(major, minor, patch);
        return true;
    }

    private static string ReadLeadingDigits(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsDigit(value[index]))
        {
            index++;
        }

        return index == 0 ? string.Empty : value[..index];
    }

    private sealed record VersionAnchor(int Major, int Minor, int Patch);

    private sealed class K8sReleaseMetadata
    {
        [JsonPropertyName("playwrightRunnerImage")]
        public string? PlaywrightRunnerImage { get; set; }
    }

    private sealed class ReleaseVersionListResponse
    {
        [JsonPropertyName("versions")]
        public List<ReleaseVersionListItem>? Versions { get; set; }
    }

    private sealed class ReleaseVersionListItem
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
