using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class BoldBiReleaseVersionProvider : IReleaseVersionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly UpgradeCenterOptions options;
    private readonly IUpgradeProductContext productContext;
    private readonly ILogger<BoldBiReleaseVersionProvider> logger;

    public BoldBiReleaseVersionProvider(
        HttpClient httpClient,
        IOptions<UpgradeCenterOptions> options,
        IUpgradeProductContext productContext,
        ILogger<BoldBiReleaseVersionProvider> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.productContext = productContext;
        this.logger = logger;
    }

    public async Task<ReleaseVersionSummary> GetReleaseVersionsAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            var product = productContext.Current;
            var releaseVersionsApiUrl = ResolveReleaseVersionsApiUrl(product);
            using var request = new HttpRequestMessage(HttpMethod.Get, releaseVersionsApiUrl);
            request.Headers.UserAgent.ParseAdd("Bold-Upgrade-Center/1.0");
            request.Headers.Accept.ParseAdd("application/json");

            logger.LogInformation("Fetching {ProductName} release versions from {ReleaseVersionsApiUrl}.", product.DisplayName, releaseVersionsApiUrl);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = $"{product.DisplayName} release API request failed with status {(int)response.StatusCode}.";
                logger.LogWarning(errorMessage);
                return new ReleaseVersionSummary(Array.Empty<ReleaseVersionInfo>(), null, VersionComparisonState.Unknown, errorMessage);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var releaseResponse = await JsonSerializer.DeserializeAsync<BoldBiReleaseVersionResponse>(stream, JsonOptions, cancellationToken)
                ?? new BoldBiReleaseVersionResponse();

            var allVersions = (releaseResponse.Versions ?? new List<BoldBiReleaseVersion>())
                .Select(ToReleaseVersionInfo)
                .Where(version => ProductVersionComparer.TryParse(version.Version, out _))
                .GroupBy(version => version.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(version => ProductVersionComparer.Parse(version.Version), ProductVersionPartsComparer.Instance)
                .ToList();

            var latestVersion = allVersions.FirstOrDefault();
            var comparison = CompareCurrentWithLatest(currentVersion, latestVersion);
            if (latestVersion is not null && !ProductVersionComparer.TryParse(currentVersion, out _))
            {
                var errorMessage = $"Unable to compare available versions because the installed {product.DisplayName} version is not in a supported version format.";
                logger.LogWarning("Installed {ProductName} version could not be parsed for upgrade comparison. CurrentVersion: {CurrentVersion}.", product.DisplayName, currentVersion);
                return new ReleaseVersionSummary(Array.Empty<ReleaseVersionInfo>(), null, VersionComparisonState.Unknown, errorMessage);
            }

            var newerVersions = FilterNewerVersions(currentVersion, allVersions);
            logger.LogInformation(
                "Fetched {VersionCount} {ProductName} release versions. NewerVersionCount: {NewerVersionCount}. LatestVersion: {LatestVersion}. ComparisonState: {ComparisonState}.",
                allVersions.Count,
                product.DisplayName,
                newerVersions.Count,
                latestVersion?.Version ?? "Unavailable",
                comparison);

            return new ReleaseVersionSummary(newerVersions, newerVersions.FirstOrDefault(), comparison, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var product = productContext.Current;
            logger.LogError(exception, "Unable to fetch {ProductName} release versions from release API.", product.DisplayName);
            return new ReleaseVersionSummary(
                Array.Empty<ReleaseVersionInfo>(),
                null,
                VersionComparisonState.Unknown,
                $"Unable to fetch available versions from {product.DisplayName} release API.");
        }
    }

    private ReleaseVersionInfo ToReleaseVersionInfo(BoldBiReleaseVersion releaseVersion)
    {
        var product = productContext.Current;
        var version = NormalizeVersion(releaseVersion.Version ?? string.Empty);
        var release = releaseVersion.Releases?
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ReleaseDate) || !string.IsNullOrWhiteSpace(item.ReleaseNotesUrl));

        return new ReleaseVersionInfo(
            version,
            version,
            $"{product.DisplayName} {version}",
            release?.ReleaseNotesUrl?.Trim() ?? string.Empty,
            ParseReleaseDate(release?.ReleaseDate));
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

    private static VersionComparisonState CompareCurrentWithLatest(string currentVersion, ReleaseVersionInfo? latestVersion)
    {
        if (latestVersion is null || !ProductVersionComparer.TryParse(currentVersion, out var currentParts))
        {
            return VersionComparisonState.Unknown;
        }

        var latestParts = ProductVersionComparer.Parse(latestVersion.Version);
        var comparison = ProductVersionComparer.Compare(currentParts, latestParts);
        return comparison < 0
            ? VersionComparisonState.UpdateAvailable
            : comparison == 0
                ? VersionComparisonState.UpToDate
                : VersionComparisonState.CurrentNewerThanAvailable;
    }

    private static IReadOnlyList<ReleaseVersionInfo> FilterNewerVersions(string currentVersion, IReadOnlyList<ReleaseVersionInfo> versions)
    {
        if (!ProductVersionComparer.TryParse(currentVersion, out var currentParts))
        {
            return Array.Empty<ReleaseVersionInfo>();
        }

        return versions
            .Where(version => ProductVersionComparer.Compare(ProductVersionComparer.Parse(version.Version), currentParts) > 0)
            .ToList();
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed[1..]
            : trimmed;
    }

    private static DateTimeOffset? ParseReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var releaseDate)
            ? releaseDate
            : null;
    }

    private sealed class BoldBiReleaseVersionResponse
    {
        [JsonPropertyName("product")]
        public string? Product { get; set; }

        [JsonPropertyName("totalVersions")]
        public int TotalVersions { get; set; }

        [JsonPropertyName("versions")]
        public List<BoldBiReleaseVersion>? Versions { get; set; }
    }

    private sealed class BoldBiReleaseVersion
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("releases")]
        public List<BoldBiRelease>? Releases { get; set; }
    }

    private sealed class BoldBiRelease
    {
        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("releaseNotesUrl")]
        public string? ReleaseNotesUrl { get; set; }
    }
}
