namespace Bold.UpgradeCenter.Services;

public static class UpgradeProductKeys
{
    public const string BoldBi = "boldbi";
    public const string BoldReports = "boldreports";
}

public enum UpgradeProduct
{
    BoldBi,
    BoldReports
}

public sealed record UpgradeProductDefinition(
    UpgradeProduct Product,
    string Key,
    string DisplayName,
    string ShortName,
    int TenantTypeId,
    string ReleaseVersionsApiUrl,
    string ProductVersionPath,
    IReadOnlyList<string> ExpectedDeployments,
    IReadOnlyList<string> OptionalDeployments,
    IReadOnlyList<IReadOnlyList<string>> RolloutGroups,
    IReadOnlyList<ProductHealthCheckTarget> HealthCheckTargets);

public interface IUpgradeProductContext
{
    UpgradeProductDefinition Current { get; }

    UpgradeProductDefinition SetCurrent(string? productKey);
}

public sealed class UpgradeProductContext : IUpgradeProductContext
{
    public UpgradeProductDefinition Current { get; private set; } = UpgradeProductDefinitions.BoldBi;

    public UpgradeProductDefinition SetCurrent(string? productKey)
    {
        Current = UpgradeProductDefinitions.Resolve(productKey);
        return Current;
    }
}

public static class UpgradeProductDefinitions
{
    public static readonly UpgradeProductDefinition BoldBi = new(
        UpgradeProduct.BoldBi,
        UpgradeProductKeys.BoldBi,
        "Bold BI",
        "BI",
        3,
        "https://releases.boldbi.com/version/json",
        "bi/api/product-version",
        [
            "bi-api-deployment",
            "bi-dataservice-deployment",
            "bi-jobs-deployment",
            "bi-web-deployment",
            "bold-ai-deployment",
            "bold-etl-deployment",
            "id-api-deployment",
            "id-ums-deployment",
            "id-web-deployment"
        ],
        [
            "bold-ai-deployment",
            "bold-etl-deployment"
        ],
        [
            ["id-web-deployment", "id-api-deployment", "id-ums-deployment"],
            ["bi-web-deployment", "bi-api-deployment", "bi-jobs-deployment"],
            ["bi-dataservice-deployment", "bold-ai-deployment", "bold-etl-deployment"]
        ],
        [
            new("health-id-web", "Identity Provider Web", "id-web-deployment", "health-check"),
            new("health-id-api", "Identity Provider API", "id-api-deployment", "api/health-check"),
            new("health-id-ums", "User Management Web", "id-ums-deployment", "ums/health-check"),
            new("health-bi-web", "Dashboard Server Web", "bi-web-deployment", "bi/health-check"),
            new("health-bi-api", "Dashboard Server API", "bi-api-deployment", "bi/api/health-check"),
            new("health-bi-jobs", "Dashboard Server Jobs", "bi-jobs-deployment", "bi/jobs/health-check"),
            new("health-bi-designer", "Dashboard Designer Service", "bi-dataservice-deployment", "bi/designer/health-check"),
            new("health-etl-service", "ETL Service", "bold-etl-deployment", "etlservice/health-check")
        ]);

    public static readonly UpgradeProductDefinition BoldReports = new(
        UpgradeProduct.BoldReports,
        UpgradeProductKeys.BoldReports,
        "Bold Reports",
        "Reports",
        4,
        "https://releases.boldbi.com/reports/version/json",
        "reporting/api/product-version",
        [
            "bold-etl-deployment",
            "id-api-deployment",
            "id-ums-deployment",
            "id-web-deployment",
            "reports-api-deployment",
            "reports-jobs-deployment",
            "reports-reportservice-deployment",
            "reports-viewer-deployment",
            "reports-web-deployment"
        ],
        [
            "bold-etl-deployment"
        ],
        [
            ["id-web-deployment", "id-api-deployment", "id-ums-deployment"],
            ["reports-api-deployment", "reports-reportservice-deployment"],
            ["reports-web-deployment", "reports-viewer-deployment", "reports-jobs-deployment", "bold-etl-deployment"]
        ],
        [
            new("health-id-web", "Identity Provider Web", "id-web-deployment", "health-check"),
            new("health-id-api", "Identity Provider API", "id-api-deployment", "api/health-check"),
            new("health-id-ums", "User Management Web", "id-ums-deployment", "ums/health-check"),
            new("health-reporting-web", "Reporting Web", "reports-web-deployment", "reporting/health-check"),
            new("health-reporting-api", "Reporting API", "reports-api-deployment", "reporting/api/health-check"),
            new("health-reporting-jobs", "Reporting Jobs", "reports-jobs-deployment", "reporting/jobs/health-check"),
            new("health-reporting-service", "Reporting Service", "reports-reportservice-deployment", "reporting/reportservice/health-check"),
            new("health-reporting-viewer", "Reporting Viewer", "reports-viewer-deployment", "reporting/viewer/health-check"),
            new("health-etl-service", "ETL Service", "bold-etl-deployment", "etlservice/health-check")
        ]);

    public static IReadOnlyList<UpgradeProductDefinition> All { get; } =
    [
        BoldBi,
        BoldReports
    ];

    public static UpgradeProductDefinition Resolve(string? productKey)
    {
        if (string.IsNullOrWhiteSpace(productKey))
        {
            return BoldBi;
        }

        return All.FirstOrDefault(product =>
            string.Equals(product.Key, productKey.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(product.DisplayName, productKey.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(product.ShortName, productKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? BoldBi;
    }
}
