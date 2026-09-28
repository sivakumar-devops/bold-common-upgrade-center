using Bold.UpgradeCenter.Services;
using Bold.UpgradeCenter.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeysPath);

// Add services.

builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUpgradeProductContext, UpgradeProductContext>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("Bold.UpgradeCenter");
builder.Services.Configure<UpgradeCenterOptions>(builder.Configuration.GetSection("UpgradeCenter"));
builder.Services.AddHttpClient(IdpConfigurationProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient(ProductVersionProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<IReleaseVersionProvider, BoldBiReleaseVersionProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient(RemoteUmsAuthenticationHandler.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IUpgradeCenterConfigurationProvider, IdpConfigurationProvider>();
builder.Services.AddScoped<IProductVersionProvider, ProductVersionProvider>();
builder.Services.AddSingleton<IAdminContinuationStore, InMemoryAdminContinuationStore>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(UpgradeCenterPolicies.AdminOnly, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(UpgradeCenterClaimTypes.IsAdmin, "true");
    });
});
builder.Services
    .AddAuthentication(UpgradeCenterAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, RemoteUmsAuthenticationHandler>(
        UpgradeCenterAuthenticationDefaults.Scheme,
        _ => { });

// Real Upgrade Center backend workflow reused from the validated Upgrade Center implementation.
builder.Services.AddHttpClient(IdpPrivateKeyProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<IUpgradeDatabaseScriptImpactService, UpgradeDatabaseScriptImpactService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IKubernetesUpgradeService, KubernetesUpgradeService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IPlaywrightRunnerImageProvider, ReleasePlaywrightRunnerImageProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient(ContainerImageRegistryValidator.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.Configure<PlaywrightExecutionOptions>(builder.Configuration.GetSection("UpgradeCenter:Playwright"));
builder.Services.Configure<DatabaseBackupOptions>(builder.Configuration.GetSection("UpgradeCenter:DatabaseBackup"));
builder.Services.Configure<KubernetesUpgradeOptions>(builder.Configuration.GetSection("UpgradeCenter:Kubernetes"));
builder.Services.Configure<ProductHealthCheckOptions>(builder.Configuration.GetSection("UpgradeCenter:ProductHealthCheck"));
builder.Services.AddSingleton<IUpgradeDeploymentModeService, UpgradeDeploymentModeService>();
builder.Services.AddHttpClient<IProductHealthCheckService, ProductHealthCheckService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IIdpPrivateKeyProvider, IdpPrivateKeyProvider>();
builder.Services.AddSingleton<IBoldConnectionStringDecryptor, BoldConnectionStringDecryptor>();
builder.Services.AddScoped<IUpgradeDatabaseDiscoveryService, UpgradeDatabaseDiscoveryService>();
builder.Services.AddScoped<IUpgradeUserContextProvider, UpgradeUserContextProvider>();
builder.Services.AddScoped<IPlaywrightKubernetesJobService, PlaywrightKubernetesJobService>();
builder.Services.AddScoped<IPlaywrightScriptRunner, PlaywrightScriptRunner>();
builder.Services.AddSingleton<IPlaywrightReportStore, PlaywrightReportStore>();
builder.Services.AddSingleton<IPlaywrightReportUploadTokenStore, InMemoryPlaywrightReportUploadTokenStore>();
builder.Services.AddSingleton<IPlaywrightValidationResultStore, InMemoryPlaywrightValidationResultStore>();
builder.Services.AddSingleton<IUpgradeOperationalStateStore, DatabaseUpgradeOperationalStateStore>();
builder.Services.AddSingleton<IUpgradeOperationLogStore, DatabaseUpgradeOperationLogStore>();
builder.Services.AddSingleton<IUpgradeRollbackStore, InMemoryUpgradeRollbackStore>();
builder.Services.AddSingleton<IUpgradeJobStore, InMemoryUpgradeJobStore>();
builder.Services.AddSingleton<IUpgradeJobCancellationManager, UpgradeJobCancellationManager>();
builder.Services.AddSingleton<IUpgradeJobExecutionRegistry, UpgradeJobExecutionRegistry>();
builder.Services.AddSingleton<IUpgradeJobRunner, UpgradeJobRunner>();
builder.Services.AddSingleton<IUpgradeRollbackJobRunner, UpgradeRollbackJobRunner>();
builder.Services.AddHostedService<UpgradeJobRecoveryService>();
builder.Services.AddScoped<IUpgradeHistoryStore, DatabaseUpgradeHistoryStore>();
builder.Services.AddScoped<IContainerImageRegistryValidator, ContainerImageRegistryValidator>();
builder.Services.AddScoped<ICustomPatchValidationService, CustomPatchValidationService>();
builder.Services.AddScoped<IUpgradeDatabaseBackupService, UpgradeDatabaseBackupService>();
builder.Services.AddScoped<IUpgradeImpactService, UpgradeImpactService>();
builder.Services.AddScoped<IUpgradeEnvironmentEligibilityService, UpgradeEnvironmentEligibilityService>();

builder.Services.AddScoped<IInstallationInfoProvider, ProductVersionInstallationInfoProvider>();
builder.Services.AddScoped<IUpgradeCenterAuthorizationService, ClaimsUpgradeCenterAuthorizationService>();
builder.Services.AddScoped<IUpgradeService, UpgradeWorkflowService>();

var app = builder.Build();
var allowedForwardedPrefixes = ResolveAllowedForwardedPrefixes(app.Configuration);

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use((context, next) => ApplyForwardedPrefix(context, next, allowedForwardedPrefixes, app.Logger));
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/upgrade-center"
});
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/health-check");
app.MapHealthChecks("/upgrade-center/health-check");

// Default route: redirect / to /upgrade-center.
app.MapControllerRoute(
    name: "upgrade-center",
    pattern: "upgrade-center/{action=Index}/{jobId?}",
    defaults: new { controller = "UpgradeCenter" });

// Root path: redirect to upgrade center.
app.MapGet("/", () => Results.Redirect("/upgrade-center"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=UpgradeCenter}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

static async Task ApplyForwardedPrefix(
    HttpContext context,
    RequestDelegate next,
    IReadOnlySet<string> allowedForwardedPrefixes,
    ILogger logger)
{
    var forwardedPrefix = context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwardedPrefix))
    {
        if (!TryNormalizeForwardedPrefix(forwardedPrefix, allowedForwardedPrefixes, out var normalizedPrefix))
        {
            logger.LogWarning("Rejected untrusted X-Forwarded-Prefix header.");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid forwarded prefix.");
            return;
        }

        if (normalizedPrefix != "/" &&
            !context.Request.PathBase.StartsWithSegments(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Request.PathBase = normalizedPrefix + context.Request.PathBase;
        }
    }

    await next(context);
}

static IReadOnlySet<string> ResolveAllowedForwardedPrefixes(IConfiguration configuration)
{
    var configuredPrefixes = configuration
        .GetSection("UpgradeCenter:Authentication:AllowedForwardedPrefixes")
        .Get<string[]>() ?? [];

    var prefixes = configuredPrefixes
        .Append("/upgrade-center")
        .Where(prefix => TryNormalizeForwardedPrefixSyntax(prefix, out _))
        .Select(prefix =>
        {
            _ = TryNormalizeForwardedPrefixSyntax(prefix, out var normalized);
            return normalized;
        })
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return prefixes.Count == 0
        ? new HashSet<string>(["/upgrade-center"], StringComparer.OrdinalIgnoreCase)
        : prefixes;
}

static bool TryNormalizeForwardedPrefix(
    string value,
    IReadOnlySet<string> allowedForwardedPrefixes,
    out string normalizedPrefix)
{
    if (!TryNormalizeForwardedPrefixSyntax(value, out normalizedPrefix))
    {
        return false;
    }

    return allowedForwardedPrefixes.Contains(normalizedPrefix);
}

static bool TryNormalizeForwardedPrefixSyntax(string value, out string normalizedPrefix)
{
    normalizedPrefix = "/";
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var trimmed = value.Trim();
    if (!trimmed.StartsWith("/", StringComparison.Ordinal) ||
        trimmed.Contains("//", StringComparison.Ordinal) ||
        trimmed.Contains("://", StringComparison.Ordinal) ||
        trimmed.Contains("..", StringComparison.Ordinal) ||
        trimmed.Contains('\\', StringComparison.Ordinal) ||
        trimmed.Contains('?', StringComparison.Ordinal) ||
        trimmed.Contains('#', StringComparison.Ordinal) ||
        trimmed.Contains(',', StringComparison.Ordinal) ||
        trimmed.Any(char.IsControl))
    {
        return false;
    }

    normalizedPrefix = trimmed.TrimEnd('/');
    if (string.IsNullOrWhiteSpace(normalizedPrefix))
    {
        normalizedPrefix = "/";
    }

    return true;
}
