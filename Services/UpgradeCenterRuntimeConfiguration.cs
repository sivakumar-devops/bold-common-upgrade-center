namespace Bold.UpgradeCenter.Services;

public sealed record UpgradeCenterRuntimeConfiguration(
    string? IdpBaseUrl,
    string? BiBaseUrl,
    string? BoldBiVersion,
    string? EncryptedMasterDatabaseConnectionString = null,
    int? MasterDatabaseServerType = null,
    string? MachineKeyDecryptionKey = null,
    string? InternalAppClientId = null,
    string? InternalAppClientSecret = null,
    bool? UseSingleTenantDb = null,
    string? MasterDatabaseSchemaName = null,
    string? MasterDatabaseTablePrefix = null,
    bool? ShowDataHub = null,
    string? ReportsBaseUrl = null,
    string? BoldReportsVersion = null);

public interface IUpgradeCenterConfigurationProvider
{
    Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
}
