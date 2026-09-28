using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public interface IContainerImageRegistryValidator
{
    Task<ContainerImageValidationResult> ValidateAsync(
        string imageReference,
        CancellationToken cancellationToken = default);
}

public sealed record ContainerImageValidationResult(
    bool IsValid,
    string Message);

public sealed class ContainerImageRegistryValidator : IContainerImageRegistryValidator
{
    public const string HttpClientName = "container-registry";
    private const int RegistryRequestTimeoutSeconds = 20;

    private static readonly MediaTypeWithQualityHeaderValue[] ManifestAcceptHeaders =
    [
        new("application/vnd.oci.image.manifest.v1+json"),
        new("application/vnd.docker.distribution.manifest.v2+json"),
        new("application/vnd.docker.distribution.manifest.list.v2+json"),
        new("application/vnd.oci.image.index.v1+json")
    ];

    private readonly IHttpClientFactory httpClientFactory;
    private readonly KubernetesUpgradeOptions kubernetesOptions;
    private readonly ILogger<ContainerImageRegistryValidator> logger;

    public ContainerImageRegistryValidator(
        IHttpClientFactory httpClientFactory,
        IOptions<KubernetesUpgradeOptions> kubernetesOptions,
        ILogger<ContainerImageRegistryValidator> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.kubernetesOptions = kubernetesOptions.Value;
        this.logger = logger;
    }

    public async Task<ContainerImageValidationResult> ValidateAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseImageReference(imageReference, out var registry, out var repository, out var tag))
        {
            return new ContainerImageValidationResult(false, "Image reference must include registry, repository, image name, and tag.");
        }

        if (!TryValidateImageReferenceParts(registry, repository, tag, out var imageReferenceError))
        {
            return new ContainerImageValidationResult(false, imageReferenceError);
        }

        var allowedRegistryHosts = GetAllowedRegistryHosts();
        if (!IsRegistryAllowed(registry, allowedRegistryHosts))
        {
            return new ContainerImageValidationResult(false, "Image registry host is not in the configured allowed registry host list.");
        }

        var registryHostValidation = await ValidateRegistryEndpointAsync(registry, allowedRegistryHosts, cancellationToken);
        if (!registryHostValidation.IsValid)
        {
            return registryHostValidation;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        if (!TryCreateManifestUri(registry, repository, tag, out var manifestUri))
        {
            return new ContainerImageValidationResult(false, "Image registry endpoint could not be constructed safely.");
        }

        try
        {
            var result = await ProbeManifestAsync(client, manifestUri, null, cancellationToken);
            if (result.StatusCode == HttpStatusCode.Unauthorized &&
                TryCreateBearerChallengeRequest(result.AuthenticateHeader, repository, out var tokenUri))
            {
                var tokenEndpointValidation = await ValidateRegistryEndpointAsync(tokenUri!, allowedRegistryHosts, cancellationToken);
                if (!tokenEndpointValidation.IsValid)
                {
                    return new ContainerImageValidationResult(false, "Registry token challenge endpoint is not in the configured allowed registry host list.");
                }

                var token = await RequestAnonymousBearerTokenAsync(client, tokenUri!, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    result = await ProbeManifestAsync(client, manifestUri, token, cancellationToken);
                }
            }

            return result.StatusCode switch
            {
                HttpStatusCode.OK => new ContainerImageValidationResult(true, "Image manifest is available."),
                HttpStatusCode.NotFound => new ContainerImageValidationResult(false, "Image tag was not found in the registry."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ContainerImageValidationResult(false, "Registry denied access to the image manifest."),
                _ => new ContainerImageValidationResult(false, $"Registry returned HTTP {(int)result.StatusCode}.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Container registry validation timed out.");
            return new ContainerImageValidationResult(false, "Container registry validation timed out.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to validate custom patch image manifest.");
            return new ContainerImageValidationResult(false, "Unable to contact the container registry for image validation.");
        }
    }

    private static async Task<ManifestProbeResult> ProbeManifestAsync(
        HttpClient client,
        Uri manifestUri,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, manifestUri);
        ApplyManifestHeaders(request, bearerToken);
        using var timeout = CreateRegistryRequestTimeout(cancellationToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            return new ManifestProbeResult(response.StatusCode, response.Headers.WwwAuthenticate.ToString());
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        ApplyManifestHeaders(getRequest, bearerToken);
        using var getTimeout = CreateRegistryRequestTimeout(cancellationToken);
        using var getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, getTimeout.Token);
        return new ManifestProbeResult(getResponse.StatusCode, getResponse.Headers.WwwAuthenticate.ToString());
    }

    private static void ApplyManifestHeaders(HttpRequestMessage request, string? bearerToken)
    {
        foreach (var header in ManifestAcceptHeaders)
        {
            request.Headers.Accept.Add(header);
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }

    private static async Task<string?> RequestAnonymousBearerTokenAsync(
        HttpClient client,
        Uri tokenUri,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateRegistryRequestTimeout(cancellationToken);
        using var response = await client.GetAsync(tokenUri, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return document.RootElement.TryGetProperty("token", out var token)
            ? token.GetString()
            : document.RootElement.TryGetProperty("access_token", out var accessToken)
                ? accessToken.GetString()
                : null;
    }

    private static bool TryCreateBearerChallengeRequest(
        string? authenticateHeader,
        string repository,
        out Uri? tokenUri)
    {
        tokenUri = null;
        if (string.IsNullOrWhiteSpace(authenticateHeader) ||
            !authenticateHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = ParseChallengeParameters(authenticateHeader["Bearer ".Length..]);
        if (!parameters.TryGetValue("realm", out var realm) ||
            !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri))
        {
            return false;
        }

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        var scope = parameters.TryGetValue("scope", out var challengeScope) && !string.IsNullOrWhiteSpace(challengeScope)
            ? challengeScope
            : $"repository:{repository}:pull";
        query.Add($"scope={Uri.EscapeDataString(scope)}");

        var builder = new UriBuilder(realmUri)
        {
            Query = string.Join("&", query)
        };
        tokenUri = builder.Uri;
        return true;
    }

    private static Dictionary<string, string> ParseChallengeParameters(string challenge)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in challenge.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            values[pair[0].Trim()] = pair[1].Trim().Trim('"');
        }

        return values;
    }

    private static bool TryValidateImageReferenceParts(
        string registry,
        string repository,
        string tag,
        out string message)
    {
        message = string.Empty;
        if (!TryValidateRegistryAuthorityFormat(registry, out message))
        {
            return false;
        }

        if (!IsRepositoryPathSyntaxValid(repository))
        {
            message = "Image repository path is malformed.";
            return false;
        }

        if (!IsTagSyntaxValid(tag))
        {
            message = "Image tag is malformed.";
            return false;
        }

        return true;
    }

    internal static bool TryParseImageReference(
        string imageReference,
        out string registry,
        out string repository,
        out string tag)
    {
        registry = string.Empty;
        repository = string.Empty;
        tag = string.Empty;

        var trimmed = imageReference.Trim();
        var slashIndex = trimmed.IndexOf('/');
        var lastSlashIndex = trimmed.LastIndexOf('/');
        var colonIndex = trimmed.LastIndexOf(':');
        if (slashIndex <= 0 || colonIndex <= lastSlashIndex || colonIndex == trimmed.Length - 1)
        {
            return false;
        }

        registry = trimmed[..slashIndex];
        repository = trimmed[(slashIndex + 1)..colonIndex];
        tag = trimmed[(colonIndex + 1)..];
        return !string.IsNullOrWhiteSpace(registry) &&
               !string.IsNullOrWhiteSpace(repository) &&
               !string.IsNullOrWhiteSpace(tag);
    }

    internal static bool TryValidateRegistryAuthorityFormat(string registry, out string message)
    {
        message = string.Empty;
        if (!TrySplitRegistryAuthority(registry, out var host, out var port))
        {
            message = "Image registry host is malformed.";
            return false;
        }

        if (!TryValidateRegistryHostAndPort(host, port, out message))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateRegistryHostAndPort(string host, int? port, out string message)
    {
        message = string.Empty;
        if (!IsHostNameSyntaxValid(host))
        {
            message = "Image registry hostname is malformed.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            message = "Image registry port is invalid.";
            return false;
        }

        if (IsLocalhost(host) ||
            IPAddress.TryParse(host, out var literalAddress) && IsBlockedAddress(literalAddress))
        {
            message = "Image registry host is not allowed.";
            return false;
        }

        return true;
    }

    private static bool TryCreateManifestUri(string registry, string repository, string tag, out Uri manifestUri)
    {
        manifestUri = null!;
        if (!TrySplitRegistryAuthority(registry, out var host, out var port) ||
            !TryValidateRegistryHostAndPort(host, port, out _) ||
            !IsRepositoryPathSyntaxValid(repository) ||
            !IsTagSyntaxValid(tag))
        {
            return false;
        }

        var builder = new UriBuilder(Uri.UriSchemeHttps, host)
        {
            Path = $"/v2/{repository}/manifests/{Uri.EscapeDataString(tag)}"
        };
        if (port.HasValue)
        {
            builder.Port = port.Value;
        }

        manifestUri = builder.Uri;
        return true;
    }

    internal static bool IsRepositoryAllowed(string repositoryWithRegistry, IEnumerable<string> allowedRepositories)
    {
        var normalizedRepository = NormalizeRepository(repositoryWithRegistry);
        return allowedRepositories
            .Where(repository => !string.IsNullOrWhiteSpace(repository))
            .Select(NormalizeRepository)
            .Any(allowed =>
                normalizedRepository.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                normalizedRepository.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<AllowedRegistryHost> GetAllowedRegistryHosts()
    {
        var hosts = new List<AllowedRegistryHost>();
        foreach (var configuredHost in kubernetesOptions.AllowedImageRegistryHosts)
        {
            if (TryParseAllowedRegistryHost(configuredHost, out var allowedHost))
            {
                hosts.Add(allowedHost);
            }
        }

        foreach (var repository in kubernetesOptions.AllowedImageRepositories)
        {
            var normalized = NormalizeRepository(repository);
            var slashIndex = normalized.IndexOf('/');
            var authority = slashIndex > 0 ? normalized[..slashIndex] : normalized;
            if (TryParseAllowedRegistryHost(authority, out var allowedHost))
            {
                hosts.Add(allowedHost);
            }
        }

        return hosts
            .Distinct()
            .ToList();
    }

    private static bool IsRegistryAllowed(string registry, IReadOnlyList<AllowedRegistryHost> allowedHosts)
    {
        if (allowedHosts.Count == 0)
        {
            return false;
        }

        return TrySplitRegistryAuthority(registry, out var host, out var port) &&
               allowedHosts.Any(allowed => allowed.Matches(host, port));
    }

    private static async Task<ContainerImageValidationResult> ValidateRegistryEndpointAsync(
        string registry,
        IReadOnlyList<AllowedRegistryHost> allowedHosts,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRegistryAuthorityFormat(registry, out var message))
        {
            return new ContainerImageValidationResult(false, message);
        }

        if (!IsRegistryAllowed(registry, allowedHosts))
        {
            return new ContainerImageValidationResult(false, "Image registry host is not in the configured allowed registry host list.");
        }

        return await ValidateHostDoesNotResolveToBlockedAddressAsync(registry, cancellationToken);
    }

    private static async Task<ContainerImageValidationResult> ValidateRegistryEndpointAsync(
        Uri uri,
        IReadOnlyList<AllowedRegistryHost> allowedHosts,
        CancellationToken cancellationToken)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerImageValidationResult(false, "Registry token challenge endpoint must use HTTPS.");
        }

        int? port = uri.IsDefaultPort ? null : uri.Port;
        if (!TryValidateRegistryHostAndPort(uri.Host, port, out var hostValidationMessage))
        {
            return new ContainerImageValidationResult(false, hostValidationMessage);
        }

        if (!allowedHosts.Any(allowed => allowed.Matches(uri.Host, port)))
        {
            return new ContainerImageValidationResult(false, "Registry token challenge endpoint is not in the configured allowed registry host list.");
        }

        return await ValidateHostDoesNotResolveToBlockedAddressAsync(uri.Host, cancellationToken);
    }

    private static async Task<ContainerImageValidationResult> ValidateHostDoesNotResolveToBlockedAddressAsync(
        string registryOrHost,
        CancellationToken cancellationToken)
    {
        var host = registryOrHost;
        if (registryOrHost.Contains(':', StringComparison.Ordinal) &&
            TrySplitRegistryAuthority(registryOrHost, out var splitHost, out _))
        {
            host = splitHost;
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            return IsBlockedAddress(literalAddress)
                ? new ContainerImageValidationResult(false, "Image registry host resolves to a local, private, link-local, or metadata address.")
                : new ContainerImageValidationResult(true, "Image registry host is allowed.");
        }

        try
        {
            using var timeout = CreateRegistryRequestTimeout(cancellationToken);
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            if (addresses.Length == 0)
            {
                return new ContainerImageValidationResult(false, "Image registry hostname could not be resolved.");
            }

            return addresses.Any(IsBlockedAddress)
                ? new ContainerImageValidationResult(false, "Image registry host resolves to a local, private, link-local, or metadata address.")
                : new ContainerImageValidationResult(true, "Image registry host is allowed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ContainerImageValidationResult(false, "Image registry hostname resolution timed out.");
        }
        catch (SocketException)
        {
            return new ContainerImageValidationResult(false, "Image registry hostname could not be resolved.");
        }
    }

    private static bool TryParseAllowedRegistryHost(string value, out AllowedRegistryHost allowedHost)
    {
        allowedHost = default!;
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            int? uriPort = uri.IsDefaultPort ? null : uri.Port;
            if (!TryValidateRegistryHostAndPort(uri.Host, uriPort, out _))
            {
                return false;
            }

            allowedHost = new AllowedRegistryHost(uri.Host.ToLowerInvariant(), uriPort);
            return true;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            trimmed = trimmed[..trimmed.IndexOf('/')];
        }

        if (!TrySplitRegistryAuthority(trimmed, out var host, out var port))
        {
            return false;
        }

        if (!TryValidateRegistryHostAndPort(host, port, out _))
        {
            return false;
        }

        allowedHost = new AllowedRegistryHost(host.ToLowerInvariant(), port);
        return true;
    }

    private static bool TrySplitRegistryAuthority(string registry, out string host, out int? port)
    {
        host = string.Empty;
        port = null;
        var trimmed = registry.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Any(char.IsWhiteSpace) ||
            trimmed.Any(char.IsControl) ||
            trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('@', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains('?', StringComparison.Ordinal) ||
            trimmed.Contains('#', StringComparison.Ordinal) ||
            trimmed.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex >= 0)
        {
            host = trimmed[..colonIndex];
            var portText = trimmed[(colonIndex + 1)..];
            if (!int.TryParse(portText, out var parsedPort))
            {
                return false;
            }

            port = parsedPort;
        }
        else
        {
            host = trimmed;
        }

        return !string.IsNullOrWhiteSpace(host);
    }

    private static bool IsHostNameSyntaxValid(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Any(char.IsWhiteSpace) ||
            host.Any(char.IsControl) ||
            host.Contains('_', StringComparison.Ordinal) ||
            host.Length > 253)
        {
            return false;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }

    private static bool IsLocalhost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 ||
                   bytes[0] == 192 && bytes[1] == 0 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19) ||
                   bytes[0] == 0 ||
                   bytes.All(value => value == 255) ||
                   bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   address.IsIPv6Multicast ||
                   address.Equals(IPAddress.IPv6Loopback) ||
                   address.Equals(IPAddress.IPv6None) ||
                   address.Equals(IPAddress.IPv6Any) ||
                   IsUniqueLocalIPv6(address);
        }

        return true;
    }

    private static bool IsUniqueLocalIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length > 0 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool IsRepositoryPathSyntaxValid(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            repository.Length > 255 ||
            repository.Contains('\\', StringComparison.Ordinal) ||
            repository.Contains("..", StringComparison.Ordinal) ||
            repository.Any(char.IsWhiteSpace) ||
            repository.Any(char.IsControl))
        {
            return false;
        }

        var segments = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
            string.Join("/", segments).Equals(repository, StringComparison.Ordinal) &&
            segments.All(segment =>
                segment.Length <= 128 &&
                segment.All(character =>
                    character is >= 'a' and <= 'z' ||
                    character is >= '0' and <= '9' ||
                    character is '.' or '_' or '-'));
    }

    private static bool IsTagSyntaxValid(string tag)
    {
        return !string.IsNullOrWhiteSpace(tag) &&
            tag.Length <= 128 &&
            (char.IsLetterOrDigit(tag[0]) || tag[0] == '_') &&
            tag.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '_' or '.' or '-');
    }

    private static CancellationTokenSource CreateRegistryRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RegistryRequestTimeoutSeconds));
        return timeout;
    }

    private static string NormalizeRepository(string repository)
    {
        var normalized = repository.Trim().TrimEnd('/');
        var digestIndex = normalized.IndexOf('@', StringComparison.Ordinal);
        if (digestIndex >= 0)
        {
            normalized = normalized[..digestIndex];
        }

        var lastSlashIndex = normalized.LastIndexOf('/');
        var lastColonIndex = normalized.LastIndexOf(':');
        if (lastColonIndex > lastSlashIndex)
        {
            normalized = normalized[..lastColonIndex];
        }

        return normalized;
    }

    private sealed record AllowedRegistryHost(string Host, int? Port)
    {
        public bool Matches(string host, int? port)
        {
            return Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                   Port == port;
        }
    }

    private sealed record ManifestProbeResult(
        HttpStatusCode StatusCode,
        string? AuthenticateHeader);
}
