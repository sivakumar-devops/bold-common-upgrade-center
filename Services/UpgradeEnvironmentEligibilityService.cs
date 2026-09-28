namespace Bold.UpgradeCenter.Services;

public interface IUpgradeEnvironmentEligibilityService
{
    Task<UpgradeEnvironmentEligibilityResult> CheckAsync(CancellationToken cancellationToken = default);

    Task EnsureUpgradeSupportedAsync(CancellationToken cancellationToken = default);
}

public sealed record UpgradeEnvironmentEligibilityResult(
    bool IsSupported,
    bool UsesSharedDatabaseConfiguration,
    bool HasOracleDatabase,
    string? WarningMessage);

public sealed class UpgradeEnvironmentEligibilityService : IUpgradeEnvironmentEligibilityService
{
    private const string SharedDatabaseWarning = "Upgrades are currently not supported for shared database configurations.";
    private const string OracleDatabaseWarning = "Upgrades are currently not supported for Oracle database configurations.";
    private const string SharedDatabaseAndOracleWarning = "Upgrades are currently not supported for shared database or Oracle database configurations.";

    private readonly IUpgradeCenterConfigurationProvider configurationProvider;
    private readonly IUpgradeDatabaseDiscoveryService databaseDiscoveryService;
    private readonly IUpgradeProductContext productContext;
    private readonly ILogger<UpgradeEnvironmentEligibilityService> logger;

    public UpgradeEnvironmentEligibilityService(
        IUpgradeCenterConfigurationProvider configurationProvider,
        IUpgradeDatabaseDiscoveryService databaseDiscoveryService,
        IUpgradeProductContext productContext,
        ILogger<UpgradeEnvironmentEligibilityService> logger)
    {
        this.configurationProvider = configurationProvider;
        this.databaseDiscoveryService = databaseDiscoveryService;
        this.productContext = productContext;
        this.logger = logger;
    }

    public async Task<UpgradeEnvironmentEligibilityResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var runtimeConfiguration = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var usesSharedDatabaseConfiguration = runtimeConfiguration.UseSingleTenantDb == true;
        var hasOracleDatabase = await HasOracleDatabaseAsync(cancellationToken, throwOnFailure: false);
        var warningMessage = BuildWarningMessage(usesSharedDatabaseConfiguration, hasOracleDatabase, productContext.Current.Product);

        return new UpgradeEnvironmentEligibilityResult(
            string.IsNullOrWhiteSpace(warningMessage),
            usesSharedDatabaseConfiguration,
            hasOracleDatabase,
            warningMessage);
    }

    public async Task EnsureUpgradeSupportedAsync(CancellationToken cancellationToken = default)
    {
        var runtimeConfiguration = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var usesSharedDatabaseConfiguration = runtimeConfiguration.UseSingleTenantDb == true;
        var hasOracleDatabase = await HasOracleDatabaseAsync(cancellationToken, throwOnFailure: true);
        var warningMessage = BuildWarningMessage(usesSharedDatabaseConfiguration, hasOracleDatabase, productContext.Current.Product);
        if (!string.IsNullOrWhiteSpace(warningMessage))
        {
            throw new InvalidOperationException(warningMessage);
        }
    }

    private async Task<bool> HasOracleDatabaseAsync(CancellationToken cancellationToken, bool throwOnFailure)
    {
        try
        {
            var databases = await databaseDiscoveryService.DiscoverDatabasesAsync(cancellationToken);
            return databases.Any(database => database.DatabaseType == DatabaseType.Oracle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to complete database eligibility validation before upgrade confirmation.");
            if (throwOnFailure)
            {
                throw new InvalidOperationException("Unable to validate the database configuration before starting the upgrade.", exception);
            }

            return false;
        }
    }

    private static string? BuildWarningMessage(bool usesSharedDatabaseConfiguration, bool hasOracleDatabase, UpgradeProduct product)
    {
        if (product == UpgradeProduct.BoldReports)
        {
            return hasOracleDatabase ? OracleDatabaseWarning : null;
        }

        return (usesSharedDatabaseConfiguration, hasOracleDatabase) switch
        {
            (true, true) => SharedDatabaseAndOracleWarning,
            (true, false) => SharedDatabaseWarning,
            (false, true) => OracleDatabaseWarning,
            _ => null
        };
    }
}
