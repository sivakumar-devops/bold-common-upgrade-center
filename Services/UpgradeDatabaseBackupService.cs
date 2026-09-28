using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeDatabaseBackupService : IUpgradeDatabaseBackupService
{
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<key>Password|Pwd|User\s*ID|Username|Token|AccessToken|Secret|Key)\s*=\s*[^;,\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IUpgradeDatabaseDiscoveryService databaseDiscoveryService;
    private readonly IUpgradeDatabaseScriptImpactService scriptImpactService;
    private readonly IUpgradeOperationLogStore operationLogStore;
    private readonly DatabaseBackupOptions backupOptions;
    private readonly IUpgradeProductContext productContext;
    private readonly ILogger<UpgradeDatabaseBackupService> logger;

    public UpgradeDatabaseBackupService(
        IUpgradeDatabaseDiscoveryService databaseDiscoveryService,
        IUpgradeDatabaseScriptImpactService scriptImpactService,
        IUpgradeOperationLogStore operationLogStore,
        IOptions<DatabaseBackupOptions> backupOptions,
        IUpgradeProductContext productContext,
        ILogger<UpgradeDatabaseBackupService> logger)
    {
        this.databaseDiscoveryService = databaseDiscoveryService;
        this.scriptImpactService = scriptImpactService;
        this.operationLogStore = operationLogStore;
        this.backupOptions = backupOptions.Value;
        this.productContext = productContext;
        this.logger = logger;
    }

    public async Task<UpgradeDatabaseBackupResult> BackupAffectedTablesAsync(string currentVersion, string selectedVersion, string? jobId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion) || selectedVersion.Equals("Not selected", StringComparison.OrdinalIgnoreCase))
        {
            return UpgradeDatabaseBackupResult.Failed(selectedVersion, "Backup skipped because no upgrade version was selected.");
        }

        try
        {
            logger.LogInformation("Starting database backup preparation for selected version {SelectedVersion}.", selectedVersion);

            var discoveredDatabases = await databaseDiscoveryService.DiscoverDatabasesAsync(cancellationToken);
            if (discoveredDatabases.Count == 0)
            {
                return UpgradeDatabaseBackupResult.Failed(selectedVersion, "No master or tenant databases were discovered for backup.");
            }

            var discoveredDatabaseTypes = discoveredDatabases
                .Select(database => database.DatabaseType)
                .Distinct()
                .OrderBy(databaseType => databaseType.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var databaseType in discoveredDatabaseTypes)
            {
                var databaseTypeName = databaseType.ToString();
                if (!TryGetDatabaseApiSegment(databaseType, out var databaseApiSegment))
                {
                    return UpgradeDatabaseBackupResult.Failed(selectedVersion, $"Database type '{databaseTypeName}' is not supported.", databaseTypeName);
                }
            }

            var scriptImpact = await scriptImpactService.AnalyzeAsync(currentVersion, selectedVersion, discoveredDatabaseTypes, cancellationToken);
            if (!scriptImpact.Succeeded)
            {
                return UpgradeDatabaseBackupResult.Failed(
                    selectedVersion,
                    $"Database schema impact analysis failed: {scriptImpact.Message}",
                    FormatDatabaseTypes(discoveredDatabases),
                    scriptImpact.AffectedTables) with
                {
                    CurrentVersion = scriptImpact.CurrentVersion,
                    ApplicableVersions = scriptImpact.ApplicableVersions,
                    DatabaseTypesAnalyzed = scriptImpact.DatabaseTypes,
                    ExistingAffectedTables = scriptImpact.ExistingAffectedTables,
                    CreatedTables = scriptImpact.CreatedTables,
                    DroppedTables = scriptImpact.DroppedTables
                };
            }

            if (!scriptImpact.HasSchemaChanges)
            {
                logger.LogInformation(
                    "No database script changes found for upgrade range {CurrentVersion} to {SelectedVersion}; table backup is not required.",
                    currentVersion,
                    selectedVersion);
                return UpgradeDatabaseBackupResult.Skipped(
                    selectedVersion,
                    scriptImpact.Message,
                    FormatDatabaseTypes(discoveredDatabases)) with
                {
                    CurrentVersion = scriptImpact.CurrentVersion,
                    ApplicableVersions = scriptImpact.ApplicableVersions,
                    DatabaseTypesAnalyzed = scriptImpact.DatabaseTypes,
                    ExistingAffectedTables = scriptImpact.ExistingAffectedTables,
                    CreatedTables = scriptImpact.CreatedTables,
                    DroppedTables = scriptImpact.DroppedTables
                };
            }

            var backupMappings = new List<DatabaseBackupMapping>();
            var allAffectedTables = scriptImpact.AffectedTables;
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var databasesRequiringBackup = discoveredDatabases.ToList();

            for (var index = 0; index < databasesRequiringBackup.Count; index++)
            {
                var discoveredDatabase = databasesRequiringBackup[index];
                var databaseType = discoveredDatabase.DatabaseType;
                var databaseTypeName = databaseType.ToString();
                var sourceConnectionString = discoveredDatabase.ConnectionString;
                var displayIndex = index + 1;
                logger.LogInformation(
                    "Preparing database backup {DatabaseIndex}/{DatabaseCount} for {DatabaseRole} database {DatabaseName} ({DatabaseType}).",
                    displayIndex,
                    databasesRequiringBackup.Count,
                    discoveredDatabase.Role,
                    discoveredDatabase.DatabaseName,
                    databaseType);

                var validation = await ValidateSourceDatabaseAsync(databaseType, sourceConnectionString, cancellationToken);
                if (!validation.Succeeded)
                {
                    backupMappings.Add(DatabaseBackupMappingFailed(
                        databaseTypeName,
                        validation.DatabaseName,
                        validation.Message,
                        sourceConnectionString,
                        discoveredDatabase.Identity));
                    logger.LogWarning(
                        "Source database validation failed for database {DatabaseName}. Reason: {Reason}",
                        validation.DatabaseName,
                        validation.Message);
                    continue;
                }

                try
                {
                    var resolvedImpact = ResolveDatabaseTableImpact(discoveredDatabase, scriptImpact);
                    AppendOperationLog(
                        jobId,
                        "Schema Backup",
                        "Info",
                        "DatabaseBackup",
                        $"Taking backup of database '{validation.DatabaseName}'.");
                    var backupPlan = await BuildDatabaseBackupPlanAsync(
                        discoveredDatabase,
                        databaseType,
                        sourceConnectionString,
                        resolvedImpact.AffectedTables,
                        resolvedImpact.CreatedTables,
                        jobId,
                        cancellationToken);
                    if (backupPlan.TablesToBackup.Count == 0)
                    {
                        if (backupPlan.CreatedTablesToDrop.Count > 0)
                        {
                            backupMappings.Add(new DatabaseBackupMapping(
                                true,
                                $"No existing affected tables required backup. Recorded {backupPlan.CreatedTablesToDrop.Count} upgrade-created table(s) for rollback cleanup.",
                                databaseTypeName,
                                validation.DatabaseName,
                                string.Empty,
                                sourceConnectionString,
                                string.Empty,
                                null,
                                discoveredDatabase.Identity,
                                backupPlan.CreatedTablesToDrop,
                                backupPlan.TablesToBackup.Select(table => table.LookupName).ToList()));
                        }

                        logger.LogInformation(
                            "Physical backup database was not created for {OriginalDatabaseName} because no matching existing affected tables were found. UpgradeCreatedTablesToDrop: {CreatedTableCount}.",
                            validation.DatabaseName,
                            backupPlan.CreatedTablesToDrop.Count);
                        continue;
                    }

                    var occurrence = IncrementNameCount(nameCounts, validation.DatabaseName);
                    var backupDatabaseName = CreateBackupDatabaseName(validation.DatabaseName, occurrence, timestamp);
                    var backupConnection = await CreateBackupConnectionStringAsync(
                        databaseType,
                        sourceConnectionString,
                        backupDatabaseName,
                        cancellationToken);

                    var progress = CreateBackupProgress(jobId, "Schema Backup");
                    var engine = new BackupEngine(progress);
                    var options = new BackupOptions
                    {
                        SourceConnectionString = sourceConnectionString,
                        TargetConnectionString = backupConnection.ConnectionString,
                        DatabaseType = databaseType,
                        IncludeTables = backupPlan.TablesToBackup.Select(table => table.LookupName).ToList(),
                        BatchSize = Math.Max(1, backupOptions.BatchSize),
                        DropAndRecreate = backupOptions.DropAndRecreate,
                        DisableFKDuringLoad = backupOptions.DisableForeignKeysDuringLoad
                    };

                    await using var source = ProviderFactory.Create(databaseType);
                    await using var target = ProviderFactory.Create(databaseType);
                    var status = await engine.RunAsync(source, target, options, cancellationToken);
                    var message = status.Success
                        ? "Database backup completed successfully."
                        : BuildBackupFailureMessage(status);

                    backupMappings.Add(new DatabaseBackupMapping(
                        status.Success,
                        message,
                        databaseTypeName,
                        validation.DatabaseName,
                        backupConnection.DatabaseName,
                        sourceConnectionString,
                        backupConnection.ConnectionString,
                        status,
                        discoveredDatabase.Identity,
                        backupPlan.CreatedTablesToDrop,
                        backupPlan.TablesToBackup.Select(table => table.LookupName).ToList()));

                    logger.LogInformation(
                        "Database backup completed for {OriginalDatabaseName}. Success: {Success}. BackupDatabase: {BackupDatabase}. Tables: {TablesSucceeded}/{TablesAttempted}. Rows: {RowsCopied}. Message: {Message}",
                        validation.DatabaseName,
                        status.Success,
                        backupConnection.DatabaseName,
                        status.TablesSucceeded,
                        status.TablesAttempted,
                        status.TotalRowsCopied,
                        message);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var safeMessage = BuildDatabaseExceptionMessage(databaseTypeName, validation.DatabaseName, exception);
                    backupMappings.Add(DatabaseBackupMappingFailed(
                        databaseTypeName,
                        validation.DatabaseName,
                        safeMessage,
                        sourceConnectionString,
                        discoveredDatabase.Identity));

                    logger.LogError(
                        exception,
                        "Database backup failed for {DatabaseType} database {DatabaseName}. SafeMessage: {SafeMessage}",
                        databaseTypeName,
                        validation.DatabaseName,
                        safeMessage);
                }
            }

            var succeeded = backupMappings.All(mapping => mapping.Succeeded);
            var physicalBackupCount = backupMappings.Count(mapping =>
                mapping.Succeeded &&
                mapping.BackupStatus is not null &&
                !string.IsNullOrWhiteSpace(mapping.BackupDatabaseName));
            var cleanupOnlyCount = backupMappings.Count(mapping =>
                mapping.Succeeded &&
                mapping.BackupStatus is null &&
                mapping.UpgradeCreatedTablesToDrop.Count > 0);
            var firstStatus = backupMappings.Select(mapping => mapping.BackupStatus).FirstOrDefault(status => status is not null);
            var firstBackup = backupMappings.FirstOrDefault(mapping =>
                mapping.Succeeded &&
                !string.IsNullOrWhiteSpace(mapping.BackupDatabaseName));
            var resultMessage = succeeded
                ? physicalBackupCount > 0
                    ? "Database backup completed successfully for all databases with matching affected tables."
                    : cleanupOnlyCount > 0
                        ? "No existing affected tables required backup. Upgrade-created tables were recorded for rollback cleanup."
                        : "No matching existing affected tables were found. Physical database backup was not required."
                : BuildBackupResultFailureMessage(backupMappings);

            return new UpgradeDatabaseBackupResult(
                succeeded,
                physicalBackupCount == 0 && cleanupOnlyCount == 0,
                resultMessage,
                selectedVersion,
                FormatDatabaseTypes(discoveredDatabases),
                allAffectedTables,
                firstStatus,
                firstBackup?.BackupDatabaseName,
                scriptImpact.ScriptPreview,
                firstBackup?.SourceConnectionString,
                firstBackup?.BackupConnectionString,
                backupMappings,
                scriptImpact.CurrentVersion,
                scriptImpact.ApplicableVersions,
                scriptImpact.DatabaseTypes,
                scriptImpact.ExistingAffectedTables,
                scriptImpact.CreatedTables,
                scriptImpact.DroppedTables);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database backup failed for selected version {SelectedVersion}.", selectedVersion);
            return UpgradeDatabaseBackupResult.Failed(selectedVersion, $"Database backup failed: {exception.Message}");
        }
    }

    public async Task<UpgradeDatabaseRestoreResult> RestoreAffectedTablesAsync(UpgradeDatabaseBackupResult backupResult, string? jobId = null, string? logStage = null, CancellationToken cancellationToken = default)
    {
        var hasCreatedTableCleanup = backupResult.BackupMappings.Any(mapping => mapping.UpgradeCreatedTablesToDrop.Count > 0);
        if (backupResult.BackupSkipped && !hasCreatedTableCleanup)
        {
            return new UpgradeDatabaseRestoreResult(true, "Database restore skipped because no database backup was required for this upgrade.", null, Array.Empty<DatabaseRestoreMapping>());
        }

        if (!backupResult.Succeeded || backupResult.BackupMappings.Count == 0)
        {
            return new UpgradeDatabaseRestoreResult(false, "Restore skipped because a successful backup is not available.", null, Array.Empty<DatabaseRestoreMapping>());
        }

        var restoreMappings = new List<DatabaseRestoreMapping>();

        try
        {
            logger.LogWarning(
                "Starting database restore for version {SelectedVersion}. DatabaseCount: {DatabaseCount}.",
                backupResult.SelectedVersion,
                backupResult.BackupMappings.Count);

            var resolvedBackupResult = await ResolveRestoreConnectionStringsAsync(backupResult, cancellationToken);
            foreach (var backupMapping in resolvedBackupResult.BackupMappings)
            {
                if (!backupMapping.Succeeded)
                {
                    restoreMappings.Add(new DatabaseRestoreMapping(
                        false,
                        "Restore skipped because this database backup was not successful.",
                        backupMapping.DatabaseType,
                        backupMapping.OriginalDatabaseName,
                        backupMapping.BackupDatabaseName,
                        null));
                    continue;
                }

                if (!TryParseDatabaseType(backupMapping.DatabaseType, out var databaseType, out _))
                {
                    restoreMappings.Add(new DatabaseRestoreMapping(
                        false,
                        $"Restore skipped because database type '{backupMapping.DatabaseType}' is not supported.",
                        backupMapping.DatabaseType,
                        backupMapping.OriginalDatabaseName,
                        backupMapping.BackupDatabaseName,
                        null));
                    continue;
                }

                var sourceValidation = await ValidateSourceDatabaseAsync(databaseType, backupMapping.SourceConnectionString, cancellationToken);
                if (!sourceValidation.Succeeded)
                {
                    restoreMappings.Add(new DatabaseRestoreMapping(
                        false,
                        $"Original database validation failed before restore: {sourceValidation.Message}",
                        backupMapping.DatabaseType,
                        backupMapping.OriginalDatabaseName,
                        backupMapping.BackupDatabaseName,
                        null));
                    continue;
                }

                var createdTableCleanup = await DropUpgradeCreatedTablesAsync(
                    databaseType,
                    backupMapping.SourceConnectionString,
                    backupMapping.UpgradeCreatedTablesToDrop,
                    jobId,
                    logStage ?? "Database restore",
                    cancellationToken);
                if (backupMapping.BackupStatus is null || string.IsNullOrWhiteSpace(backupMapping.BackupDatabaseName))
                {
                    restoreMappings.Add(new DatabaseRestoreMapping(
                        createdTableCleanup.Succeeded,
                        createdTableCleanup.Succeeded
                            ? $"Rollback cleanup completed. {createdTableCleanup.Message} No backed-up table restore was required."
                            : $"Rollback cleanup failed. {createdTableCleanup.Message}",
                        backupMapping.DatabaseType,
                        backupMapping.OriginalDatabaseName,
                        backupMapping.BackupDatabaseName,
                        null));
                    continue;
                }

                var backupValidation = await ValidateSourceDatabaseAsync(databaseType, backupMapping.BackupConnectionString, cancellationToken);
                if (!backupValidation.Succeeded)
                {
                    restoreMappings.Add(new DatabaseRestoreMapping(
                        false,
                        $"Backup database validation failed before restore: {backupValidation.Message}",
                        backupMapping.DatabaseType,
                        backupMapping.OriginalDatabaseName,
                        backupMapping.BackupDatabaseName,
                        null));
                    continue;
                }

                var progress = CreateBackupProgress(jobId, logStage ?? "Database restore");
                var engine = new RestoreEngine(progress);
                var options = new RestoreOptions
                {
                    BackupConnectionString = backupMapping.BackupConnectionString,
                    TargetConnectionString = backupMapping.SourceConnectionString,
                    DatabaseType = databaseType,
                    IncludeTables = backupMapping.BackupTableNames.Count > 0
                        ? backupMapping.BackupTableNames
                        : resolvedBackupResult.AffectedTables,
                    BatchSize = Math.Max(1, backupOptions.BatchSize),
                    DropAndRecreate = true,
                    DisableFKDuringLoad = backupOptions.DisableForeignKeysDuringLoad
                };

                await using var backup = ProviderFactory.Create(databaseType);
                await using var target = ProviderFactory.Create(databaseType);
                var status = await engine.RunAsync(backup, target, options, cancellationToken);
                var mappingSucceeded = createdTableCleanup.Succeeded && status.Success;
                var message = mappingSucceeded
                    ? "Database restore completed successfully."
                    : createdTableCleanup.Succeeded
                        ? "Database restore failed."
                        : $"Database restore failed because upgrade-created table cleanup did not complete. {createdTableCleanup.Message}";

                restoreMappings.Add(new DatabaseRestoreMapping(
                    mappingSucceeded,
                    message,
                    backupMapping.DatabaseType,
                    backupMapping.OriginalDatabaseName,
                    backupMapping.BackupDatabaseName,
                    status));

                logger.LogWarning(
                    "Database restore completed for {OriginalDatabaseName} from {BackupDatabaseName}. Success: {Success}. Tables: {TablesSucceeded}/{TablesAttempted}. Rows: {RowsRestored}.",
                    backupMapping.OriginalDatabaseName,
                    backupMapping.BackupDatabaseName,
                    mappingSucceeded,
                    status.TablesSucceeded,
                    status.TablesAttempted,
                    status.TotalRowsRestored);
            }

            var succeeded = restoreMappings.Count == resolvedBackupResult.BackupMappings.Count && restoreMappings.All(mapping => mapping.Succeeded);
            var firstStatus = restoreMappings.Select(mapping => mapping.RestoreStatus).FirstOrDefault(status => status is not null);
            var resultMessage = succeeded
                ? "Database restore completed successfully for all databases."
                : "Database restore failed for one or more databases.";

            return new UpgradeDatabaseRestoreResult(succeeded, resultMessage, firstStatus, restoreMappings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database restore failed for selected version {SelectedVersion}.", backupResult.SelectedVersion);
            restoreMappings.Add(new DatabaseRestoreMapping(false, $"Database restore failed: {exception.Message}", backupResult.DatabaseType ?? "Unknown", "Unknown", "Unknown", null));
            return new UpgradeDatabaseRestoreResult(false, $"Database restore failed: {exception.Message}", null, restoreMappings);
        }
    }

    public async Task<BackupCleanupResult> CleanupBackupDatabasesAsync(UpgradeDatabaseBackupResult backupResult, CancellationToken cancellationToken = default)
    {
        if (backupResult.BackupSkipped || backupResult.BackupMappings.Count == 0)
        {
            return new BackupCleanupResult(true, "Backup cleanup skipped because no physical backup databases were recorded.", Array.Empty<BackupCleanupMapping>());
        }

        var cleanupMappings = new List<BackupCleanupMapping>();
        try
        {
            var resolvedBackupResult = await ResolveRestoreConnectionStringsAsync(backupResult, cancellationToken);
            var physicalBackupMappings = resolvedBackupResult.BackupMappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.BackupDatabaseName))
                .ToList();
            if (physicalBackupMappings.Count == 0)
            {
                return new BackupCleanupResult(true, "Backup cleanup skipped because no physical backup databases were recorded.", Array.Empty<BackupCleanupMapping>());
            }

            foreach (var mapping in physicalBackupMappings)
            {
                if (!TryParseDatabaseType(mapping.DatabaseType, out var databaseType, out _))
                {
                    cleanupMappings.Add(new BackupCleanupMapping(
                        false,
                        $"Cleanup skipped because database type '{mapping.DatabaseType}' is not supported.",
                        mapping.DatabaseType,
                        mapping.OriginalDatabaseName,
                        mapping.BackupDatabaseName));
                    continue;
                }

                try
                {
                    await DropBackupDatabaseAsync(databaseType, mapping.SourceConnectionString, mapping.BackupDatabaseName, cancellationToken);
                    cleanupMappings.Add(new BackupCleanupMapping(
                        true,
                        "Backup database cleanup completed.",
                        mapping.DatabaseType,
                        mapping.OriginalDatabaseName,
                        mapping.BackupDatabaseName));
                    logger.LogInformation(
                        "Backup cleanup completed for backup database {BackupDatabaseName} ({DatabaseType}).",
                        mapping.BackupDatabaseName,
                        mapping.DatabaseType);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var safeMessage = ToSafeDatabaseMessage(exception.Message);
                    cleanupMappings.Add(new BackupCleanupMapping(
                        false,
                        $"Backup cleanup failed: {safeMessage}",
                        mapping.DatabaseType,
                        mapping.OriginalDatabaseName,
                        mapping.BackupDatabaseName));
                    logger.LogWarning(
                        exception,
                        "Backup cleanup failed for backup database {BackupDatabaseName} ({DatabaseType}). SafeMessage: {SafeMessage}",
                        mapping.BackupDatabaseName,
                        mapping.DatabaseType,
                        safeMessage);
                }
            }

            var succeeded = cleanupMappings.Count > 0 && cleanupMappings.All(mapping => mapping.Succeeded);
            var message = succeeded
                ? "Backup cleanup completed for all superseded backup databases."
                : "Backup cleanup did not complete for one or more superseded backup databases.";
            return new BackupCleanupResult(succeeded, message, cleanupMappings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var safeMessage = ToSafeDatabaseMessage(exception.Message);
            logger.LogWarning(exception, "Backup cleanup failed. SafeMessage: {SafeMessage}", safeMessage);
            return new BackupCleanupResult(false, $"Backup cleanup failed: {safeMessage}", cleanupMappings);
        }
    }

    private ResolvedDatabaseTableImpact ResolveDatabaseTableImpact(
        DiscoveredDatabaseConnection discoveredDatabase,
        UpgradeDatabaseScriptImpactResult scriptImpact)
    {
        if (scriptImpact.TableImpacts.Count == 0)
        {
            return new ResolvedDatabaseTableImpact(
                scriptImpact.AffectedTables.Select(table => ResolvedPhysicalTable.Unknown(discoveredDatabase, table)).ToList(),
                scriptImpact.CreatedTables.Select(table => ResolvedPhysicalTable.Unknown(discoveredDatabase, table)).ToList());
        }

        var affectedTables = new Dictionary<string, ResolvedPhysicalTable>(StringComparer.OrdinalIgnoreCase);
        var createdTables = new Dictionary<string, ResolvedPhysicalTable>(StringComparer.OrdinalIgnoreCase);

        foreach (var tableImpact in scriptImpact.TableImpacts)
        {
            var physicalTables = ResolvePhysicalTableNames(
                discoveredDatabase,
                tableImpact.Component,
                tableImpact.LogicalTableName,
                tableImpact.DefaultTablePrefix,
                tableImpact.SchemaName,
                tableImpact.SourcePrefix,
                tableImpact.BaseTableName,
                tableImpact.ChangeType,
                productContext.Current.Product);

            foreach (var physicalTable in physicalTables)
            {
                affectedTables.TryAdd(physicalTable.LookupName, physicalTable);

                if (tableImpact.ChangeType.Equals("created", StringComparison.OrdinalIgnoreCase))
                {
                    createdTables.TryAdd(physicalTable.LookupName, physicalTable);
                }
            }
        }

        return new ResolvedDatabaseTableImpact(
            affectedTables.Values.OrderBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase).ToList(),
            createdTables.Values.OrderBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IReadOnlyList<ResolvedPhysicalTable> ResolvePhysicalTableNames(
        DiscoveredDatabaseConnection discoveredDatabase,
        string component,
        string logicalTableName,
        string defaultTablePrefix,
        string? schemaName,
        string? sourcePrefix,
        string? baseTableName,
        string changeType,
        UpgradeProduct product)
    {
        var tableName = NormalizePhysicalTableName(logicalTableName);
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return Array.Empty<ResolvedPhysicalTable>();
        }

        var fallbackPrefix = string.IsNullOrWhiteSpace(defaultTablePrefix)
            ? GetDefaultTablePrefix(component, product)
            : defaultTablePrefix.Trim();
        var configuredPrefix = string.IsNullOrWhiteSpace(discoveredDatabase.TablePrefix)
            ? fallbackPrefix
            : discoveredDatabase.TablePrefix.Trim();
        var resolvedBaseTableName = string.IsNullOrWhiteSpace(baseTableName)
            ? RemoveKnownTablePrefix(tableName, component)
            : baseTableName.Trim();

        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTableNameCandidate(candidateNames, tableName);
        if (!string.IsNullOrWhiteSpace(resolvedBaseTableName))
        {
            AddTableNameCandidate(candidateNames, ReplaceKnownTablePrefix(tableName, component, fallbackPrefix));
            if (!string.IsNullOrWhiteSpace(sourcePrefix))
            {
                AddTableNameCandidate(candidateNames, sourcePrefix.Trim() + resolvedBaseTableName);
            }

            if (ShouldApplyConfiguredPrefix(component, configuredPrefix))
            {
                AddTableNameCandidate(candidateNames, configuredPrefix + resolvedBaseTableName);
            }
        }

        var resolvedSchema = ResolveSchemaName(discoveredDatabase, schemaName);

        return candidateNames
            .Select(candidateName =>
            {
                var lookupName = FormatLookupTableName(discoveredDatabase.DatabaseType, resolvedSchema, candidateName);
                var displayName = string.IsNullOrWhiteSpace(resolvedSchema)
                    ? candidateName
                    : $"{resolvedSchema}.{candidateName}";

                return new ResolvedPhysicalTable(
                    lookupName,
                    displayName,
                    component,
                    changeType,
                    resolvedSchema,
                    configuredPrefix,
                    sourcePrefix ?? string.Empty,
                    discoveredDatabase.DatabaseName,
                    discoveredDatabase.DatabaseType.ToString());
            })
            .OrderBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddTableNameCandidate(ISet<string> candidateNames, string? tableName)
    {
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            candidateNames.Add(tableName.Trim());
        }
    }

    private static bool ShouldApplyConfiguredPrefix(string component, string configuredPrefix)
    {
        if (string.IsNullOrWhiteSpace(configuredPrefix))
        {
            return false;
        }

        var knownPrefixes = GetKnownTablePrefixes("Unknown");
        var componentPrefixes = GetKnownTablePrefixes(component);
        var isKnownDifferentComponentPrefix = knownPrefixes.Any(prefix => prefix.Equals(configuredPrefix, StringComparison.OrdinalIgnoreCase)) &&
                                             !componentPrefixes.Any(prefix => prefix.Equals(configuredPrefix, StringComparison.OrdinalIgnoreCase));

        return !isKnownDifferentComponentPrefix;
    }

    private static string NormalizePhysicalTableName(string tableName)
    {
        var trimmed = tableName.Trim().TrimEnd(';', ',');
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actualTableName = parts.Length == 0 ? trimmed : parts[^1];
        return actualTableName.Trim('"', '`', '[', ']');
    }

    private static string ReplaceKnownTablePrefix(string tableName, string component, string actualPrefix)
    {
        foreach (var knownPrefix in GetKnownTablePrefixes(component))
        {
            if (tableName.StartsWith(knownPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return actualPrefix + tableName[knownPrefix.Length..];
            }
        }

        return tableName;
    }

    private static string RemoveKnownTablePrefix(string tableName, string component)
    {
        foreach (var knownPrefix in GetKnownTablePrefixes(component))
        {
            if (tableName.StartsWith(knownPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return tableName[knownPrefix.Length..];
            }
        }

        return tableName;
    }

    private static bool HasKnownTablePrefix(string tableName, string component)
    {
        return GetKnownTablePrefixes(component)
            .Any(prefix => tableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveSchemaName(DiscoveredDatabaseConnection discoveredDatabase, string? scriptSchemaName)
    {
        var schema = !string.IsNullOrWhiteSpace(discoveredDatabase.SchemaName)
            ? discoveredDatabase.SchemaName
            : scriptSchemaName;
        schema = schema?.Trim();
        return string.IsNullOrWhiteSpace(schema) ||
               schema.Equals("database_name", StringComparison.OrdinalIgnoreCase)
            ? null
            : schema;
    }

    private static string FormatLookupTableName(DatabaseType databaseType, string? schemaName, string tableName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return tableName;
        }

        var defaultSchema = databaseType switch
        {
            DatabaseType.MSSQL => "dbo",
            DatabaseType.PostgreSQL => "public",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(defaultSchema) &&
               schemaName.Equals(defaultSchema, StringComparison.OrdinalIgnoreCase)
            ? tableName
            : $"{schemaName}.{tableName}";
    }

    private static IReadOnlyList<string> GetKnownTablePrefixes(string component)
    {
        if (component.Equals("IDP", StringComparison.OrdinalIgnoreCase))
        {
            return ["BOLDTC_"];
        }

        if (component.Equals("Server", StringComparison.OrdinalIgnoreCase))
        {
            return ["BOLDBI_", "SyncDS_", "BOLDRS_", "SyncRS_"];
        }

        return ["BOLDTC_", "BOLDBI_", "SyncDS_", "BOLDRS_", "SyncRS_"];
    }

    private static string GetDefaultTablePrefix(string component, UpgradeProduct product)
    {
        if (component.Equals("IDP", StringComparison.OrdinalIgnoreCase))
        {
            return "BOLDTC_";
        }

        if (component.Equals("Server", StringComparison.OrdinalIgnoreCase))
        {
            return product == UpgradeProduct.BoldReports ? "BOLDRS_" : "BOLDBI_";
        }

        return string.Empty;
    }

    private async Task<DatabaseBackupPlan> BuildDatabaseBackupPlanAsync(
        DiscoveredDatabaseConnection discoveredDatabase,
        DatabaseType databaseType,
        string sourceConnectionString,
        IReadOnlyList<ResolvedPhysicalTable> affectedTables,
        IReadOnlyList<ResolvedPhysicalTable> createdTables,
        string? jobId,
        CancellationToken cancellationToken)
    {
        await using var source = ProviderFactory.Create(databaseType);
        await source.OpenAsync(sourceConnectionString, cancellationToken);
        var availableTables = await source.GetTableNamesAsync(cancellationToken);
        var existingTables = availableTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingTableLookup = availableTables
            .GroupBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var tablesToBackup = affectedTables
            .Where(table => existingTableLookup.ContainsKey(table.LookupName))
            .GroupBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var table = group.First();
                return table with { LookupName = existingTableLookup[table.LookupName] };
            })
            .OrderBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingAffectedTables = affectedTables
            .Where(table => !existingTables.Contains(table.LookupName))
            .GroupBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(table => table.LookupName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var missingTable in missingAffectedTables)
        {
            AppendOperationLog(
                jobId,
                "Schema Backup",
                "Warning",
                "DatabaseBackup",
                $"Table '{missingTable.DisplayName}' is not available in database '{discoveredDatabase.DatabaseName}' and will be skipped.");
        }

        if (tablesToBackup.Count == 0)
        {
            AppendOperationLog(
                jobId,
                "Schema Backup",
                "Warning",
                "DatabaseBackup",
                $"No matching affected tables were found in database '{discoveredDatabase.DatabaseName}'. Backup database creation was skipped.");
        }

        var createdTablesToDrop = createdTables
            .Where(table => !existingTables.Contains(table.LookupName))
            .Select(table => table.LookupName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var table in createdTables.Where(table => existingTables.Contains(table.LookupName)))
        {
            AppendOperationLog(
                jobId,
                "Schema Backup",
                "Info",
                "DatabaseBackup",
                $"Table '{table.DisplayName}' already exists in database '{discoveredDatabase.DatabaseName}'. Backup will be used for rollback if required.");
        }

        foreach (var table in createdTables.Where(table => !existingTables.Contains(table.LookupName)))
        {
            AppendOperationLog(
                jobId,
                "Schema Backup",
                "Info",
                "DatabaseBackup",
                $"Table '{table.DisplayName}' is not available in database '{discoveredDatabase.DatabaseName}'. It will be treated as upgrade-created and removed during rollback if required.");
        }

        return new DatabaseBackupPlan(tablesToBackup, createdTablesToDrop);
    }

    private static string FormatComponentName(string component)
    {
        return string.IsNullOrWhiteSpace(component) || component.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? "database"
            : component;
    }

    private static string FormatSchemaName(string? schemaName)
    {
        return string.IsNullOrWhiteSpace(schemaName) ? "default" : schemaName;
    }

    private static string FormatPrefix(string? prefix)
    {
        return string.IsNullOrWhiteSpace(prefix) ? "default" : prefix;
    }

    private async Task<CreatedTableCleanupResult> DropUpgradeCreatedTablesAsync(
        DatabaseType databaseType,
        string sourceConnectionString,
        IReadOnlyList<string> createdTablesToDrop,
        string? jobId,
        string logStage,
        CancellationToken cancellationToken)
    {
        if (createdTablesToDrop.Count == 0)
        {
            return new CreatedTableCleanupResult(true, "No upgrade-created tables were recorded for cleanup.");
        }

        var failedTables = new List<string>();
        await using var target = ProviderFactory.Create(databaseType);
        await target.OpenAsync(sourceConnectionString, cancellationToken);
        foreach (var table in createdTablesToDrop.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AppendOperationLog(jobId, logStage, "Info", "DatabaseRestore", $"Dropping upgrade-created table '{table}' before restoring backed-up tables.");
                await target.DropTableIfExistsAsync(table, cancellationToken);
                AppendOperationLog(jobId, logStage, "Info", "DatabaseRestore", $"Upgrade-created table '{table}' was removed successfully.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var safeMessage = ToSafeDatabaseMessage(exception.Message);
                failedTables.Add(table);
                AppendOperationLog(jobId, logStage, "Error", "DatabaseRestore", $"Failed to remove upgrade-created table '{table}': {safeMessage}");
                logger.LogError(
                    exception,
                    "Failed to remove upgrade-created table {TableName} during rollback. SafeMessage: {SafeMessage}",
                    table,
                    safeMessage);
            }
        }

        return failedTables.Count == 0
            ? new CreatedTableCleanupResult(true, $"Removed {createdTablesToDrop.Count} upgrade-created table(s).")
            : new CreatedTableCleanupResult(false, $"Failed to remove upgrade-created table(s): {string.Join(", ", failedTables)}.");
    }

    private async Task<UpgradeDatabaseBackupResult> ResolveRestoreConnectionStringsAsync(
        UpgradeDatabaseBackupResult backupResult,
        CancellationToken cancellationToken)
    {
        if (backupResult.BackupMappings.All(mapping =>
                !string.IsNullOrWhiteSpace(mapping.SourceConnectionString) &&
                !string.IsNullOrWhiteSpace(mapping.BackupConnectionString)))
        {
            return backupResult;
        }

        var discoveredDatabases = await databaseDiscoveryService.DiscoverDatabasesAsync(cancellationToken);
        var resolvedMappings = new List<DatabaseBackupMapping>();
        foreach (var mapping in backupResult.BackupMappings)
        {
            if (!string.IsNullOrWhiteSpace(mapping.SourceConnectionString) &&
                !string.IsNullOrWhiteSpace(mapping.BackupConnectionString))
            {
                resolvedMappings.Add(mapping);
                continue;
            }

            if (!TryParseDatabaseType(mapping.DatabaseType, out var databaseType, out _))
            {
                resolvedMappings.Add(mapping);
                continue;
            }

            var discoveredDatabase = ResolveDiscoveredDatabase(discoveredDatabases, mapping, databaseType);
            if (discoveredDatabase is null)
            {
                resolvedMappings.Add(mapping with
                {
                    Succeeded = false,
                    Message = $"Restore skipped because original database '{mapping.OriginalDatabaseName}' could not be rediscovered."
                });
                continue;
            }

            var backupConnectionString = string.IsNullOrWhiteSpace(mapping.BackupDatabaseName)
                ? string.Empty
                : BuildConnectionStringForDatabase(
                    databaseType,
                    discoveredDatabase.ConnectionString,
                    mapping.BackupDatabaseName);

            resolvedMappings.Add(mapping with
            {
                SourceConnectionString = discoveredDatabase.ConnectionString,
                BackupConnectionString = backupConnectionString,
                OriginalDatabaseIdentifier = discoveredDatabase.Identity
            });
        }

        return backupResult with
        {
            SourceConnectionString = resolvedMappings.FirstOrDefault(mapping => !string.IsNullOrWhiteSpace(mapping.SourceConnectionString))?.SourceConnectionString,
            BackupConnectionString = resolvedMappings.FirstOrDefault(mapping => !string.IsNullOrWhiteSpace(mapping.BackupConnectionString))?.BackupConnectionString,
            DatabaseBackups = resolvedMappings
        };
    }

    private static DiscoveredDatabaseConnection? ResolveDiscoveredDatabase(
        IReadOnlyList<DiscoveredDatabaseConnection> discoveredDatabases,
        DatabaseBackupMapping mapping,
        DatabaseType databaseType)
    {
        if (!string.IsNullOrWhiteSpace(mapping.OriginalDatabaseIdentifier))
        {
            var byIdentity = discoveredDatabases.FirstOrDefault(database =>
                database.DatabaseType == databaseType &&
                string.Equals(database.Identity, mapping.OriginalDatabaseIdentifier, StringComparison.OrdinalIgnoreCase));
            if (byIdentity is not null)
            {
                return byIdentity;
            }
        }

        return discoveredDatabases.FirstOrDefault(database =>
            database.DatabaseType == databaseType &&
            string.Equals(database.DatabaseName, mapping.OriginalDatabaseName, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildConnectionStringForDatabase(DatabaseType databaseType, string sourceConnectionString, string databaseName)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => BuildPostgreSqlConnectionString(sourceConnectionString, databaseName),
            DatabaseType.MSSQL => BuildSqlServerConnectionString(sourceConnectionString, databaseName),
            DatabaseType.MySQL => BuildMySqlConnectionString(sourceConnectionString, databaseName),
            DatabaseType.Oracle => BuildOracleConnectionString(sourceConnectionString, databaseName),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported.")
        };
    }

    private static string BuildPostgreSqlConnectionString(string sourceConnectionString, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

    private static string BuildSqlServerConnectionString(string sourceConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(sourceConnectionString)
        {
            InitialCatalog = databaseName
        };
        return builder.ConnectionString;
    }

    private static string BuildMySqlConnectionString(string sourceConnectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

    private static string BuildOracleConnectionString(string sourceConnectionString, string databaseName)
    {
        var builder = new OracleConnectionStringBuilder(sourceConnectionString)
        {
            UserID = databaseName
        };
        return builder.ConnectionString;
    }

    private static Task DropBackupDatabaseAsync(
        DatabaseType databaseType,
        string sourceConnectionString,
        string backupDatabaseName,
        CancellationToken cancellationToken)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => DropPostgreSqlBackupDatabaseAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            DatabaseType.MSSQL => DropSqlServerBackupDatabaseAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            DatabaseType.MySQL => DropMySqlBackupDatabaseAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            DatabaseType.Oracle => DropOracleBackupSchemaAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported.")
        };
    }

    private static async Task DropPostgreSqlBackupDatabaseAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var terminateCommand = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid();",
            connection))
        {
            terminateCommand.Parameters.AddWithValue("databaseName", backupDatabaseName);
            await terminateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var dropCommand = new NpgsqlCommand($"DROP DATABASE IF EXISTS {QuotePostgreSqlIdentifier(backupDatabaseName)};", connection);
        await dropCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropSqlServerBackupDatabaseAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var adminBuilder = new SqlConnectionStringBuilder(sourceConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var databaseName = QuoteSqlServerIdentifier(backupDatabaseName);
        await using var command = new SqlCommand($"""
            IF DB_ID(@databaseName) IS NOT NULL
            BEGIN
                ALTER DATABASE {databaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {databaseName};
            END
            """, connection);
        command.Parameters.AddWithValue("@databaseName", backupDatabaseName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropMySqlBackupDatabaseAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var adminBuilder = new MySqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = string.Empty
        };

        await using var connection = new MySqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand($"DROP DATABASE IF EXISTS {QuoteMySqlIdentifier(backupDatabaseName)};", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropOracleBackupSchemaAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var backupSchemaName = backupDatabaseName.ToUpperInvariant();
        await using var connection = new OracleConnection(sourceConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = new OracleCommand("SELECT USERNAME FROM ALL_USERS WHERE USERNAME = :schemaName", connection);
        existsCommand.Parameters.Add(new OracleParameter("schemaName", backupSchemaName));
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (exists is null)
        {
            return;
        }

        await using var dropCommand = new OracleCommand($"DROP USER {QuoteOracleIdentifier(backupSchemaName)} CASCADE", connection);
        await dropCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryGetDatabaseApiSegment(DatabaseType databaseType, out string apiSegment)
    {
        apiSegment = databaseType switch
        {
            DatabaseType.PostgreSQL => "postgresql",
            DatabaseType.MySQL => "mysql",
            DatabaseType.MSSQL => "mssql",
            DatabaseType.Oracle => "oracle",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(apiSegment);
    }

    private static string FormatDatabaseTypes(IReadOnlyList<DiscoveredDatabaseConnection> databases)
    {
        return string.Join(", ", databases
            .Select(database => database.DatabaseType.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(databaseType => databaseType, StringComparer.OrdinalIgnoreCase));
    }

    private static DatabaseBackupMapping DatabaseBackupMappingFailed(
        string databaseType,
        string originalDatabaseName,
        string message,
        string sourceConnectionString,
        string? originalDatabaseIdentifier = null)
    {
        return new DatabaseBackupMapping(
            false,
            message,
            databaseType,
            originalDatabaseName,
            string.Empty,
            sourceConnectionString,
            string.Empty,
            null,
            originalDatabaseIdentifier);
    }

    private static string BuildBackupFailureMessage(BackupStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.FatalError))
        {
            return $"Database backup failed: {ToSafeDatabaseMessage(status.FatalError)}";
        }

        var failedTables = status.TableResults
            .Where(table => !table.Success)
            .Take(3)
            .Select(table =>
            {
                var tableError = string.IsNullOrWhiteSpace(table.ErrorMessage)
                    ? "No table-level error was returned."
                    : ToSafeDatabaseMessage(table.ErrorMessage);
                return $"{table.TableName}: {tableError}";
            })
            .ToList();

        if (failedTables.Count > 0)
        {
            return $"Database backup failed for {status.TablesFailed} table(s). {string.Join(" | ", failedTables)}";
        }

        return "Database backup failed, but no table-level error was returned. Check the Upgrade Center pod logs for the provider exception.";
    }

    private static string BuildBackupResultFailureMessage(IReadOnlyList<DatabaseBackupMapping> backupMappings)
    {
        var failedMappings = backupMappings
            .Where(mapping => !mapping.Succeeded)
            .Take(3)
            .Select(mapping => $"{mapping.DatabaseType} {mapping.OriginalDatabaseName}: {ToSafeDatabaseMessage(mapping.Message)}")
            .ToList();

        if (failedMappings.Count == 0)
        {
            return "Database backup failed for one or more databases. Upgrade should not continue.";
        }

        return $"Database backup failed for one or more databases. {string.Join(" | ", failedMappings)}";
    }

    private static string BuildDatabaseExceptionMessage(string databaseType, string databaseName, Exception exception)
    {
        return $"{databaseType} backup failed for database/schema '{databaseName}': {ToSafeDatabaseMessage(exception.Message)}";
    }

    private static string ToSafeDatabaseMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No additional details were returned.";
        }

        var sanitized = SensitiveAssignmentPattern.Replace(message, match => $"{match.Groups["key"].Value}=***");
        return sanitized.Length <= 1200 ? sanitized : sanitized[..1200] + "...";
    }

    private async Task<DatabaseValidationResult> ValidateSourceDatabaseAsync(
        DatabaseType databaseType,
        string connectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            return databaseType switch
            {
                DatabaseType.PostgreSQL => await ValidatePostgreSqlDatabaseAsync(connectionString, cancellationToken),
                DatabaseType.MSSQL => await ValidateSqlServerDatabaseAsync(connectionString, cancellationToken),
                DatabaseType.MySQL => await ValidateMySqlDatabaseAsync(connectionString, cancellationToken),
                DatabaseType.Oracle => await ValidateOracleDatabaseAsync(connectionString, cancellationToken),
                _ => new DatabaseValidationResult(false, "Unknown", $"Database type '{databaseType}' is not supported.")
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database validation failed for {DatabaseType}.", databaseType);
            return new DatabaseValidationResult(false, "Unknown", $"Connection validation failed: {exception.Message}");
        }
    }

    private static async Task<DatabaseValidationResult> ValidatePostgreSqlDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            return new DatabaseValidationResult(false, "Unknown", "PostgreSQL connection string does not contain a database name.");
        }

        var databaseName = builder.Database;
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        };

        await using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync(cancellationToken);
            await using var existsCommand = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @databaseName;", adminConnection);
            existsCommand.Parameters.AddWithValue("databaseName", databaseName);
            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is null)
            {
                return new DatabaseValidationResult(false, databaseName, $"Source database '{databaseName}' does not exist.");
            }
        }

        await using var sourceConnection = new NpgsqlConnection(connectionString);
        await sourceConnection.OpenAsync(cancellationToken);
        return new DatabaseValidationResult(true, databaseName, "Source database exists and is accessible.");
    }

    private static async Task<DatabaseValidationResult> ValidateSqlServerDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            return new DatabaseValidationResult(false, "Unknown", "SQL Server connection string does not contain a database name.");
        }

        var databaseName = builder.InitialCatalog;
        var adminBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };

        await using (var adminConnection = new SqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync(cancellationToken);
            await using var existsCommand = new SqlCommand("SELECT DB_ID(@databaseName);", adminConnection);
            existsCommand.Parameters.AddWithValue("@databaseName", databaseName);
            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is null || exists == DBNull.Value)
            {
                return new DatabaseValidationResult(false, databaseName, $"Source database '{databaseName}' does not exist.");
            }
        }

        await using var sourceConnection = new SqlConnection(connectionString);
        await sourceConnection.OpenAsync(cancellationToken);
        return new DatabaseValidationResult(true, databaseName, "Source database exists and is accessible.");
    }

    private static async Task<DatabaseValidationResult> ValidateMySqlDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            return new DatabaseValidationResult(false, "Unknown", "MySQL connection string does not contain a database name.");
        }

        var databaseName = builder.Database;
        var adminBuilder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = string.Empty
        };

        await using (var adminConnection = new MySqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync(cancellationToken);
            await using var existsCommand = new MySqlCommand("SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @databaseName;", adminConnection);
            existsCommand.Parameters.AddWithValue("@databaseName", databaseName);
            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is null)
            {
                return new DatabaseValidationResult(false, databaseName, $"Source database '{databaseName}' does not exist.");
            }
        }

        await using var sourceConnection = new MySqlConnection(connectionString);
        await sourceConnection.OpenAsync(cancellationToken);
        return new DatabaseValidationResult(true, databaseName, "Source database exists and is accessible.");
    }

    private static async Task<DatabaseValidationResult> ValidateOracleDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        var databaseName = !string.IsNullOrWhiteSpace(builder.UserID) ? builder.UserID : builder.DataSource;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return new DatabaseValidationResult(false, "Unknown", "Oracle connection string does not contain a user/schema or data source.");
        }

        await using var sourceConnection = new OracleConnection(connectionString);
        await sourceConnection.OpenAsync(cancellationToken);
        return new DatabaseValidationResult(true, databaseName, "Oracle source schema exists and is accessible.");
    }

    private async Task<BackupConnectionInfo> CreateBackupConnectionStringAsync(
        DatabaseType databaseType,
        string sourceConnectionString,
        string backupDatabaseName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating backup database {BackupDatabaseName} for {DatabaseType}.", backupDatabaseName, databaseType);

        return databaseType switch
        {
            DatabaseType.PostgreSQL => await CreatePostgreSqlBackupDatabaseAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            DatabaseType.MSSQL => await CreateSqlServerBackupDatabaseAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            DatabaseType.MySQL => await CreateMySqlBackupDatabaseAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            DatabaseType.Oracle => await CreateOracleBackupSchemaAsync(sourceConnectionString, backupDatabaseName, cancellationToken),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported.")
        };
    }

    private static async Task<BackupConnectionInfo> CreatePostgreSqlBackupDatabaseAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var sourceBuilder = new NpgsqlConnectionStringBuilder(sourceConnectionString);
        var adminBuilder = new NpgsqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @databaseName;", connection);
        existsCommand.Parameters.AddWithValue("databaseName", backupDatabaseName);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (exists is null)
        {
            await using var createCommand = new NpgsqlCommand($"CREATE DATABASE {QuotePostgreSqlIdentifier(backupDatabaseName)};", connection);
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        sourceBuilder.Database = backupDatabaseName;
        return new BackupConnectionInfo(sourceBuilder.ConnectionString, backupDatabaseName);
    }

    private static async Task<BackupConnectionInfo> CreateSqlServerBackupDatabaseAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
        var adminBuilder = new SqlConnectionStringBuilder(sourceConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = new SqlCommand("SELECT DB_ID(@databaseName);", connection);
        existsCommand.Parameters.AddWithValue("@databaseName", backupDatabaseName);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (exists is null || exists == DBNull.Value)
        {
            await using var createCommand = new SqlCommand($"CREATE DATABASE {QuoteSqlServerIdentifier(backupDatabaseName)};", connection);
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        sourceBuilder.InitialCatalog = backupDatabaseName;
        return new BackupConnectionInfo(sourceBuilder.ConnectionString, backupDatabaseName);
    }

    private static async Task<BackupConnectionInfo> CreateMySqlBackupDatabaseAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var sourceBuilder = new MySqlConnectionStringBuilder(sourceConnectionString);
        var adminBuilder = new MySqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = string.Empty
        };

        await using var connection = new MySqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var createCommand = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS {QuoteMySqlIdentifier(backupDatabaseName)};", connection);
        await createCommand.ExecuteNonQueryAsync(cancellationToken);

        sourceBuilder.Database = backupDatabaseName;
        return new BackupConnectionInfo(sourceBuilder.ConnectionString, backupDatabaseName);
    }

    private static async Task<BackupConnectionInfo> CreateOracleBackupSchemaAsync(string sourceConnectionString, string backupDatabaseName, CancellationToken cancellationToken)
    {
        var sourceBuilder = new OracleConnectionStringBuilder(sourceConnectionString);
        if (string.IsNullOrWhiteSpace(sourceBuilder.Password))
        {
            throw new InvalidOperationException("Oracle backup schema creation requires a password in the source connection string.");
        }

        var backupSchemaName = backupDatabaseName.ToUpperInvariant();
        await using var connection = new OracleConnection(sourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        var defaultTablespace = await GetOracleDefaultTablespaceAsync(connection, cancellationToken);

        await using var existsCommand = new OracleCommand("SELECT USERNAME FROM ALL_USERS WHERE USERNAME = :schemaName", connection);
        existsCommand.Parameters.Add(new OracleParameter("schemaName", backupSchemaName));
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (exists is null)
        {
            var tablespaceClause = string.IsNullOrWhiteSpace(defaultTablespace)
                ? string.Empty
                : $" DEFAULT TABLESPACE {QuoteOracleIdentifier(defaultTablespace)} QUOTA UNLIMITED ON {QuoteOracleIdentifier(defaultTablespace)}";
            await using var createUser = new OracleCommand($"CREATE USER {QuoteOracleIdentifier(backupSchemaName)} IDENTIFIED BY {QuoteOraclePassword(sourceBuilder.Password)}{tablespaceClause}", connection);
            await createUser.ExecuteNonQueryAsync(cancellationToken);

            await using var grantConnect = new OracleCommand($"GRANT CONNECT, RESOURCE TO {QuoteOracleIdentifier(backupSchemaName)}", connection);
            await grantConnect.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(defaultTablespace))
        {
            await using var grantQuota = new OracleCommand($"ALTER USER {QuoteOracleIdentifier(backupSchemaName)} QUOTA UNLIMITED ON {QuoteOracleIdentifier(defaultTablespace)}", connection);
            await grantQuota.ExecuteNonQueryAsync(cancellationToken);
        }

        sourceBuilder.UserID = backupSchemaName;
        return new BackupConnectionInfo(sourceBuilder.ConnectionString, backupSchemaName);
    }

    private static async Task<string?> GetOracleDefaultTablespaceAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new OracleCommand("SELECT DEFAULT_TABLESPACE FROM USER_USERS", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value?.ToString();
    }

    private static int IncrementNameCount(Dictionary<string, int> counts, string databaseName)
    {
        counts.TryGetValue(databaseName, out var count);
        count++;
        counts[databaseName] = count;
        return count;
    }

    private static string CreateBackupDatabaseName(string originalDatabaseName, int occurrence, string timestamp)
    {
        var safeName = Regex.Replace(originalDatabaseName, "[^A-Za-z0-9_]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "database";
        }

        return occurrence <= 1
            ? $"{safeName}_backup_{timestamp}"
            : $"{safeName}_{occurrence}_backup_{timestamp}";
    }

    private static bool TryParseDatabaseType(string value, out DatabaseType databaseType, out string apiSegment)
    {
        var normalized = value.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        (databaseType, apiSegment) = normalized switch
        {
            "postgresql" or "postgres" or "pgsql" => (DatabaseType.PostgreSQL, "postgresql"),
            "mysql" or "mariadb" => (DatabaseType.MySQL, "mysql"),
            "mssql" or "sqlserver" or "microsoftsqlserver" => (DatabaseType.MSSQL, "mssql"),
            "oracle" or "ora" => (DatabaseType.Oracle, "oracle"),
            _ => ((DatabaseType)(-1), string.Empty)
        };

        return !string.IsNullOrWhiteSpace(apiSegment);
    }

    private static string QuotePostgreSqlIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuoteSqlServerIdentifier(string identifier)
    {
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private static string QuoteMySqlIdentifier(string identifier)
    {
        return "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    private static string QuoteOracleIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuoteOraclePassword(string password)
    {
        return "\"" + password.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private IBackupProgress CreateBackupProgress(string? jobId, string stage)
    {
        return new LoggerBackupProgress(logger, operationLogStore, jobId, stage);
    }

    private void AppendOperationLog(string? jobId, string stage, string level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        operationLogStore.Append(jobId, stage, level, source, ToSafeDatabaseMessage(message));
    }

    private sealed record DatabaseValidationResult(bool Succeeded, string DatabaseName, string Message);

    private sealed record BackupConnectionInfo(string ConnectionString, string DatabaseName);

    private sealed record ResolvedDatabaseTableImpact(
        IReadOnlyList<ResolvedPhysicalTable> AffectedTables,
        IReadOnlyList<ResolvedPhysicalTable> CreatedTables);

    private sealed record ResolvedPhysicalTable(
        string LookupName,
        string DisplayName,
        string Component,
        string ChangeType,
        string? SchemaName,
        string ConfiguredPrefix,
        string SourcePrefix,
        string DatabaseName,
        string DatabaseType)
    {
        public static ResolvedPhysicalTable Unknown(DiscoveredDatabaseConnection database, string tableName)
        {
            return new ResolvedPhysicalTable(
                tableName,
                tableName,
                "Unknown",
                "altered",
                database.SchemaName,
                database.TablePrefix ?? string.Empty,
                string.Empty,
                database.DatabaseName,
                database.DatabaseType.ToString());
        }
    }

    private sealed record DatabaseBackupPlan(
        IReadOnlyList<ResolvedPhysicalTable> TablesToBackup,
        IReadOnlyList<string> CreatedTablesToDrop);

    private sealed record CreatedTableCleanupResult(bool Succeeded, string Message);

    private sealed class LoggerBackupProgress : IBackupProgress
    {
        private readonly ILogger logger;
        private readonly IUpgradeOperationLogStore operationLogStore;
        private readonly string? jobId;
        private readonly string stage;
        private int currentTableIndex;
        private int totalTables;

        public LoggerBackupProgress(
            ILogger logger,
            IUpgradeOperationLogStore operationLogStore,
            string? jobId,
            string stage)
        {
            this.logger = logger;
            this.operationLogStore = operationLogStore;
            this.jobId = jobId;
            this.stage = stage;
        }

        public void OnPhaseStarted(string phase)
        {
            logger.LogInformation("Database backup phase: {Phase}", phase);
        }

        public void OnTableStarted(string tableName, int tableIndex, int totalTables)
        {
            this.currentTableIndex = tableIndex;
            this.totalTables = totalTables;
            logger.LogInformation("Processing table {TableName} ({TableIndex}/{TotalTables}).", tableName, tableIndex, totalTables);
        }

        public void OnTableProgress(string tableName, long rowsCopied)
        {
            logger.LogDebug("Processed {RowsCopied} rows from table {TableName}.", rowsCopied, tableName);
        }

        public void OnTableCompleted(TableBackupResult result)
        {
            logger.LogInformation(
                "Database table copy completed for {TableName}. Success: {Success}. Rows: {RowsCopied}. Elapsed: {Elapsed}. Error: {ErrorMessage}",
                result.TableName,
                result.Success,
                result.RowsCopied,
                result.Elapsed,
                result.ErrorMessage);
            Append(
                result.Success ? "Info" : "Error",
                "DatabaseBackup",
                result.Success
                    ? $"Table '{result.TableName}' {GetOperationName()} completed. Rows: {result.RowsCopied}. {FormatTableProgress()}."
                    : $"Table '{result.TableName}' {GetOperationName()} failed. {FormatTableProgress()}. {result.ErrorMessage}");
        }

        public void OnWarning(string message)
        {
            logger.LogWarning("Database backup warning: {Message}", message);
            Append("Warning", "DatabaseBackup", message);
        }

        public void OnError(string context, Exception ex)
        {
            logger.LogError(ex, "Database backup error at {Context}.", context);
            Append("Error", "DatabaseBackup", $"Database backup error at {context}: {ex.Message}");
        }

        private void Append(string level, string source, string message)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            operationLogStore.Append(jobId, stage, level, source, ToSafeDatabaseMessage(message));
        }

        private string FormatTableProgress()
        {
            return totalTables > 0
                ? $"{Math.Clamp(currentTableIndex, 0, totalTables)} of {totalTables}"
                : "Table progress unavailable";
        }

        private string GetOperationName()
        {
            return stage.Contains("restore", StringComparison.OrdinalIgnoreCase)
                ? "restore"
                : "backup";
        }
    }
}
