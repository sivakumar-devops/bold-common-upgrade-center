using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bold.UpgradeCenter.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Security;

public sealed class RemoteUmsAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string HttpClientName = "UpgradeCenterUmsAuthentication";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly IOptions<UpgradeCenterOptions> upgradeCenterOptions;
    private readonly IUpgradeCenterConfigurationProvider upgradeCenterConfigurationProvider;
    private readonly IAdminContinuationStore adminContinuationStore;

    public RemoteUmsAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<UpgradeCenterOptions> upgradeCenterOptions,
        IUpgradeCenterConfigurationProvider upgradeCenterConfigurationProvider,
        IAdminContinuationStore adminContinuationStore)
        : base(options, logger, encoder)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.upgradeCenterOptions = upgradeCenterOptions;
        this.upgradeCenterConfigurationProvider = upgradeCenterConfigurationProvider;
        this.adminContinuationStore = adminContinuationStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieHeader = Request.Headers.Cookie.ToString();
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return AuthenticateResult.NoResult();
        }

        var sessionFingerprint = CreateSessionFingerprint(Request.Cookies);
        if (string.IsNullOrWhiteSpace(sessionFingerprint))
        {
            return AuthenticateResult.NoResult();
        }

        var umsUrl = ResolveUmsUrl();
        if (string.IsNullOrWhiteSpace(umsUrl))
        {
            Logger.LogWarning("UMS internal URL is not configured. Upgrade Center cannot validate the current session.");
            return AuthenticateResult.NoResult();
        }

        try
        {
            var validation = await ValidateSessionAsync(umsUrl, cookieHeader, Context.RequestAborted);
            if (validation.Status == UmsValidationStatus.Unavailable)
            {
                return AuthenticateFromContinuation(sessionFingerprint, "UMS session validation is temporarily unavailable.");
            }

            if (validation.Session is null || !validation.Session.IsAuthenticated)
            {
                adminContinuationStore.Clear(sessionFingerprint);
                return AuthenticateResult.NoResult();
            }

            if (!validation.Session.IsAdmin)
            {
                adminContinuationStore.Clear(sessionFingerprint);
                return CreateAuthenticationTicket(validation.Session);
            }

            var userName = ResolveUserName(validation.Session.UserName, validation.Session.DisplayName, validation.Session.Email);
            adminContinuationStore.Remember(
                sessionFingerprint,
                new AdminContinuationUser(
                    validation.Session.UserId ?? string.Empty,
                    userName,
                    validation.Session.Email,
                    DateTimeOffset.UtcNow));

            return CreateAuthenticationTicket(validation.Session);
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning(ex, "UMS session validation timed out.");
            return AuthenticateFromContinuation(sessionFingerprint, "UMS session validation timed out.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to validate Upgrade Center session through UMS.");
            return AuthenticateFromContinuation(sessionFingerprint, "UMS session validation failed.");
        }
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var runtimeConfiguration = await upgradeCenterConfigurationProvider.GetConfigurationAsync(Context.RequestAborted);
        var loginUrl = ResolveLoginUrl(runtimeConfiguration.IdpBaseUrl);
        var returnUrl = ResolveReturnUrl(runtimeConfiguration.IdpBaseUrl);
        var separator = loginUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        Response.Redirect($"{loginUrl}{separator}ReturnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect($"{Request.PathBase}/upgrade-center/unauthorized");
        return Task.CompletedTask;
    }

    private AuthenticateResult CreateAuthenticationTicket(UpgradeCenterSessionValidationResult session)
    {
        var userName = ResolveUserName(session.UserName, session.DisplayName, session.Email);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId ?? string.Empty),
            new(ClaimTypes.Name, userName),
            new(UpgradeCenterClaimTypes.IsAdmin, session.IsAdmin ? "true" : "false")
        };

        if (!string.IsNullOrWhiteSpace(userName))
        {
            claims.Add(new Claim(UpgradeCenterClaimTypes.UserName, userName));
        }

        if (!string.IsNullOrWhiteSpace(session.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, session.Email));
        }

        if (session.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private AuthenticateResult AuthenticateFromContinuation(string sessionFingerprint, string reason)
    {
        if (!adminContinuationStore.TryGet(sessionFingerprint, out var user))
        {
            Logger.LogWarning("UMS validation is unavailable and no local admin continuation is available. Reason: {Reason}", reason);
            return AuthenticateResult.NoResult();
        }

        Logger.LogWarning(
            "Using pod-lifetime admin continuation while UMS validation is unavailable. Reason: {Reason}. ValidatedAt: {ValidatedAt}.",
            reason,
            user.ValidatedAt);

        return CreateAuthenticationTicket(new UpgradeCenterSessionValidationResult(
            true,
            true,
            user.UserId,
            user.UserName,
            user.Email,
            user.UserName));
    }

    private async Task<UmsValidationResult> ValidateSessionAsync(
        string umsUrl,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var sessionPath = FirstConfiguredValue(
            upgradeCenterOptions.Value.Authentication.SessionValidationPath,
            configuration["UpgradeCenter:Authentication:SessionValidationPath"]) ?? "/upgrade-center/auth/session";

        using var sessionResponse = await SendCookieValidationRequestAsync(umsUrl, sessionPath, cookieHeader, cancellationToken);
        if (sessionResponse.StatusCode == HttpStatusCode.OK)
        {
            await using var stream = await sessionResponse.Content.ReadAsStreamAsync(cancellationToken);
            var session = await JsonSerializer.DeserializeAsync<UpgradeCenterSessionValidationResult>(
                stream,
                JsonSerializerOptions,
                cancellationToken);
            return session is null || session.Status == false
                ? new UmsValidationResult(UmsValidationStatus.Completed, null)
                : new UmsValidationResult(UmsValidationStatus.Completed, session);
        }

        if (sessionResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new UmsValidationResult(UmsValidationStatus.Completed, null);
        }

        if (IsUnavailableStatus(sessionResponse.StatusCode))
        {
            Logger.LogWarning("UMS session validation endpoint is unavailable. StatusCode: {StatusCode}.", (int)sessionResponse.StatusCode);
            return new UmsValidationResult(UmsValidationStatus.Unavailable, null);
        }

        if (sessionResponse.StatusCode != HttpStatusCode.NotFound)
        {
            return new UmsValidationResult(UmsValidationStatus.Completed, null);
        }

        Logger.LogWarning("UMS session validation endpoint was not found. Falling back to the older admin validation endpoint.");
        return await ValidateWithAdminEndpointAsync(umsUrl, cookieHeader, cancellationToken);
    }

    private async Task<UmsValidationResult> ValidateWithAdminEndpointAsync(
        string umsUrl,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var adminPath = FirstConfiguredValue(
            upgradeCenterOptions.Value.Authentication.AdminValidationPath,
            configuration["UpgradeCenter:Authentication:AdminValidationPath"]) ?? "/upgrade-center/authorization/admin";

        using var adminResponse = await SendCookieValidationRequestAsync(umsUrl, adminPath, cookieHeader, cancellationToken);
        if (adminResponse.StatusCode == HttpStatusCode.OK)
        {
            var adminValidation = await ReadAdminValidationResultAsync(adminResponse, cancellationToken);
            var isAdmin = adminValidation?.IsAdmin ?? adminValidation?.Status ?? true;
            return new UmsValidationResult(
                UmsValidationStatus.Completed,
                new UpgradeCenterSessionValidationResult(true, isAdmin, null, null, null, null));
        }

        if (adminResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            return new UmsValidationResult(
                UmsValidationStatus.Completed,
                new UpgradeCenterSessionValidationResult(true, false, null, null, null, null));
        }

        if (adminResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new UmsValidationResult(UmsValidationStatus.Completed, null);
        }

        if (IsUnavailableStatus(adminResponse.StatusCode))
        {
            Logger.LogWarning("UMS admin validation endpoint is unavailable. StatusCode: {StatusCode}.", (int)adminResponse.StatusCode);
            return new UmsValidationResult(UmsValidationStatus.Unavailable, null);
        }

        return new UmsValidationResult(UmsValidationStatus.Completed, null);
    }

    private static async Task<UpgradeCenterAdminValidationResult?> ReadAdminValidationResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<UpgradeCenterAdminValidationResult>(
                stream,
                JsonSerializerOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsUnavailableStatus(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        return status == 408 || status == 429 || status >= 500;
    }

    private static string? CreateSessionFingerprint(IRequestCookieCollection cookies)
    {
        var stableCookies = cookies
            .Where(cookie => !IsVolatileCookie(cookie.Key) && !string.IsNullOrWhiteSpace(cookie.Value))
            .OrderBy(cookie => cookie.Key, StringComparer.Ordinal)
            .Select(cookie => $"{cookie.Key}={cookie.Value}");
        var material = string.Join(";", stableCookies);
        if (string.IsNullOrWhiteSpace(material))
        {
            return null;
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    private static bool IsVolatileCookie(string cookieName)
    {
        return cookieName.StartsWith(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase) ||
               cookieName.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendCookieValidationRequestAsync(
        string umsUrl,
        string path,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CombineUrl(umsUrl, path));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        var client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private string? ResolveUmsUrl()
    {
        return FirstConfiguredValue(
            upgradeCenterOptions.Value.Services.Ums,
            configuration["UpgradeCenter:Services:Ums"],
            configuration["UPGRADE_CENTER_UMS_INTERNAL_URL"],
            configuration["ID_UMS_SERVICE_URL"])?.TrimEnd('/');
    }

    private string ResolveLoginUrl(string? idpBaseUrl)
    {
        var loginPath = FirstConfiguredValue(
            upgradeCenterOptions.Value.Authentication.LoginPath,
            configuration["UpgradeCenter:Authentication:LoginPath"]) ?? "/accounts/login";

        if (TryCreateHttpUri(loginPath, out var absoluteLoginUri))
        {
            return absoluteLoginUri.ToString();
        }

        if (TryCreateHttpUri(idpBaseUrl, out var idpUri))
        {
            var relativeLoginPath = RemoveBasePath(loginPath, idpUri.AbsolutePath);
            return CombineUrl(idpBaseUrl!, relativeLoginPath);
        }

        return $"{Request.Scheme}://{Request.Host}{NormalizePath(loginPath)}";
    }

    private string ResolveReturnUrl(string? idpBaseUrl)
    {
        var pathAndQuery = ResolveSafeApplicationPathAndQuery();
        if (TryCreateHttpUri(idpBaseUrl, out var idpUri))
        {
            var publicPathAndQuery = EnsureBasePath(pathAndQuery, idpUri.AbsolutePath);
            return $"{idpUri.GetLeftPart(UriPartial.Authority)}{publicPathAndQuery}";
        }

        return pathAndQuery;
    }

    private string ResolveSafeApplicationPathAndQuery()
    {
        var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value! : string.Empty;
        var path = Request.Path.HasValue ? Request.Path.Value! : "/upgrade-center";
        var query = Request.QueryString.HasValue ? Request.QueryString.Value! : string.Empty;
        var pathAndQuery = $"{pathBase}{path}{query}";

        return IsSafeApplicationPathAndQuery(pathAndQuery)
            ? pathAndQuery
            : "/upgrade-center";
    }

    private bool IsSafeApplicationPathAndQuery(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery) ||
            pathAndQuery.Contains("://", StringComparison.Ordinal) ||
            pathAndQuery.Contains("//", StringComparison.Ordinal) ||
            pathAndQuery.Contains("..", StringComparison.Ordinal) ||
            pathAndQuery.Contains('\\', StringComparison.Ordinal) ||
            pathAndQuery.Contains('#', StringComparison.Ordinal) ||
            pathAndQuery.Any(char.IsControl) ||
            Uri.TryCreate(pathAndQuery, UriKind.Absolute, out _))
        {
            return false;
        }

        var pathOnly = pathAndQuery.Split('?', 2)[0];
        if (!pathOnly.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedPath = NormalizePath(pathOnly);
        return normalizedPath.Equals("/upgrade-center", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("/upgrade-center/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.EndsWith("/upgrade-center", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/upgrade-center/", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveBasePath(string loginPath, string idpBasePath)
    {
        if (string.IsNullOrWhiteSpace(idpBasePath) || idpBasePath == "/")
        {
            return loginPath;
        }

        return loginPath.StartsWith(idpBasePath, StringComparison.OrdinalIgnoreCase)
            ? loginPath[idpBasePath.Length..]
            : loginPath;
    }

    private static string EnsureBasePath(string pathAndQuery, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || basePath == "/")
        {
            return pathAndQuery;
        }

        var normalizedBasePath = NormalizePath(basePath);
        var normalizedPathAndQuery = NormalizePath(pathAndQuery);
        if (normalizedPathAndQuery.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase) ||
            normalizedPathAndQuery.StartsWith(normalizedBasePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return pathAndQuery;
        }

        return CombinePath(normalizedBasePath, pathAndQuery);
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static bool TryCreateHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static string CombinePath(string basePath, string pathAndQuery)
    {
        return $"{basePath.TrimEnd('/')}/{pathAndQuery.TrimStart('/')}";
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "/";
        }

        return value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string ResolveUserName(params string?[] values)
    {
        foreach (var value in values)
        {
            var userName = NormalizeUserName(value);
            if (!string.IsNullOrWhiteSpace(userName) && !Guid.TryParse(userName, out _))
            {
                return userName;
            }
        }

        return "Bold BI user";
    }

    private static string? NormalizeUserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var userName = value.Trim();
        var slashIndex = userName.LastIndexOf(" / ", StringComparison.Ordinal);
        if (slashIndex >= 0 && slashIndex + 3 < userName.Length)
        {
            userName = userName[(slashIndex + 3)..].Trim();
        }

        var emailSeparator = userName.IndexOf('@', StringComparison.Ordinal);
        if (emailSeparator > 0)
        {
            userName = userName[..emailSeparator].Trim();
        }

        return string.IsNullOrWhiteSpace(userName) ? null : userName;
    }
}

public sealed record UpgradeCenterSessionValidationResult(
    bool IsAuthenticated,
    bool IsAdmin,
    string? UserId,
    string? UserName,
    string? Email,
    string? DisplayName,
    bool? Status = null);

public sealed record UpgradeCenterAdminValidationResult(
    bool? Status,
    bool? IsAdmin);

internal enum UmsValidationStatus
{
    Completed,
    Unavailable
}

internal sealed record UmsValidationResult(
    UmsValidationStatus Status,
    UpgradeCenterSessionValidationResult? Session);
