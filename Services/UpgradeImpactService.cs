namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeImpactService : IUpgradeImpactService
{
    private const int MaxDisplayedTables = 20;

    private readonly IInstallationInfoProvider installationInfoProvider;
    private readonly IUpgradeDatabaseDiscoveryService databaseDiscoveryService;
    private readonly IUpgradeDatabaseScriptImpactService scriptImpactService;
    private readonly ILogger<UpgradeImpactService> logger;

    public UpgradeImpactService(
        IInstallationInfoProvider installationInfoProvider,
        IUpgradeDatabaseDiscoveryService databaseDiscoveryService,
        IUpgradeDatabaseScriptImpactService scriptImpactService,
        ILogger<UpgradeImpactService> logger)
    {
        this.installationInfoProvider = installationInfoProvider;
        this.databaseDiscoveryService = databaseDiscoveryService;
        this.scriptImpactService = scriptImpactService;
        this.logger = logger;
    }

    public async Task<UpgradeImpactResult> GetImpactAsync(string selectedVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion) ||
            selectedVersion.Equals("Not selected", StringComparison.OrdinalIgnoreCase))
        {
            return Unknown(selectedVersion, "Select a target version to check database schema impact.");
        }

        try
        {
            var currentVersion = await installationInfoProvider.GetInstallationInfoAsync(cancellationToken);
            var discoveredDatabases = await databaseDiscoveryService.DiscoverDatabasesAsync(cancellationToken);
            if (discoveredDatabases.Count == 0)
            {
                return Unknown(selectedVersion, "Database schema impact could not be determined because no master or tenant databases were discovered.");
            }

            var databaseTypes = discoveredDatabases
                .Select(database => database.DatabaseType)
                .Distinct()
                .OrderBy(databaseType => databaseType.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var impact = await scriptImpactService.AnalyzeAsync(currentVersion.InstalledVersion, selectedVersion, databaseTypes, cancellationToken);
            if (!impact.Succeeded)
            {
                return new UpgradeImpactResult(
                    selectedVersion,
                    false,
                    false,
                    databaseTypes.Select(databaseType => databaseType.ToString()).ToList(),
                    Array.Empty<string>(),
                    0,
                    BuildWarnings(impact.Message));
            }

            var orderedTables = impact.AffectedTables
                .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var warnings = new List<string>();
            if (impact.HasSchemaChanges)
            {
                warnings.Add($"Database schema changes are expected across {impact.ApplicableVersions.Count} applicable version(s). A backup will be required before the upgrade continues.");
                warnings.Add($"Unique affected tables identified: {orderedTables.Count}.");
            }
            else
            {
                warnings.Add("No database schema changes were reported by the version provider for the selected upgrade range.");
            }

            warnings.Add($"Upgrade range analyzed: {FormatDisplayVersion(impact.CurrentVersion)} to {FormatDisplayVersion(impact.TargetVersion)}.");
            if (impact.ApplicableVersions.Count > 0)
            {
                warnings.Add($"Applicable versions: {string.Join(", ", impact.ApplicableVersions.Select(FormatDisplayVersion))}.");
            }

            if (impact.DatabaseTypes.Count > 0)
            {
                warnings.Add($"Database types analyzed: {string.Join(", ", impact.DatabaseTypes)}.");
            }

            warnings.Add("Pre-upgrade validation runs before backup and Kubernetes image changes.");
            warnings.Add("If post-upgrade validation fails, the rollback workflow restores the database backup and previous Kubernetes images.");

            return new UpgradeImpactResult(
                selectedVersion,
                true,
                impact.HasSchemaChanges,
                impact.DatabaseTypes,
                orderedTables.Take(MaxDisplayedTables).ToList(),
                orderedTables.Count,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to determine upgrade impact for selected version {SelectedVersion}.", selectedVersion);
            return Unknown(selectedVersion, "Database schema impact could not be determined before confirmation. The backup step will validate it again before any upgrade changes.");
        }
    }

    private static UpgradeImpactResult Unknown(string selectedVersion, string warning)
    {
        return new UpgradeImpactResult(
            selectedVersion,
            false,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            0,
            BuildWarnings(warning));
    }

    private static IReadOnlyList<string> BuildWarnings(string warning)
    {
        return new[]
        {
            warning,
            "Pre-upgrade validation runs before backup and Kubernetes image changes.",
            "If post-upgrade validation fails, the rollback workflow restores the database backup and previous Kubernetes images."
        };
    }

    private static string FormatDisplayVersion(string version)
    {
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }
}
