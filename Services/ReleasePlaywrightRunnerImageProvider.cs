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

        var uri = BuildVersionApiUri(selectedVersion.Trim(), "k8s");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Bold-Upgrade-Center/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Playwright runner image metadata request failed. StatusCode: {StatusCode}. Version: {Version}.",
                    (int)response.StatusCode,
                    selectedVersion);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var metadata = await JsonSerializer.DeserializeAsync<K8sReleaseMetadata>(stream, JsonOptions, cancellationToken);
            return string.IsNullOrWhiteSpace(metadata?.PlaywrightRunnerImage)
                ? null
                : metadata.PlaywrightRunnerImage.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to resolve Playwright runner image from release metadata. Version: {Version}.",
                selectedVersion);
            return null;
        }
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

    private sealed class K8sReleaseMetadata
    {
        [JsonPropertyName("playwrightRunnerImage")]
        public string? PlaywrightRunnerImage { get; set; }
    }
}
