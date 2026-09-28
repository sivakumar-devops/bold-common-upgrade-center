namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeCenterOptions
{
    public string ProductName { get; set; } = "Bold Upgrade Center";

    public string DeploymentMode { get; set; } = "Auto";

    public string BiReleaseVersionsApiUrl { get; set; } = "https://releases.boldbi.com/version/json";

    public string ReleaseVersionsApiUrl { get; set; } = string.Empty;

    public string ReportsReleaseVersionsApiUrl { get; set; } = "https://releases.boldbi.com/reports/version/json";

    public UpgradeCenterServiceOptions Services { get; set; } = new();

    public UpgradeCenterAuthenticationOptions Authentication { get; set; } = new();
}

public sealed class UpgradeCenterServiceOptions
{
    public string? IdpApi { get; set; }

    public string? Ums { get; set; }
}

public sealed class UpgradeCenterAuthenticationOptions
{
    public string LoginPath { get; set; } = "/accounts/login";

    public string SessionValidationPath { get; set; } = "/upgrade-center/auth/session";

    public string AdminValidationPath { get; set; } = "/upgrade-center/authorization/admin";

    public string? IdpPublicUrlOverride { get; set; }

    public List<string> AllowedForwardedPrefixes { get; set; } =
    [
        "/upgrade-center",
        "/platform",
        "/platform/upgrade-center"
    ];
}
