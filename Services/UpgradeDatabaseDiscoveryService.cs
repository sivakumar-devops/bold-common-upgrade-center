using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public interface IUpgradeDatabaseDiscoveryService
{
    Task<DiscoveredDatabaseConnection> DiscoverMasterDatabaseAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscoveredDatabaseConnection>> DiscoverDatabasesAsync(CancellationToken cancellationToken = default);
}

public sealed record DiscoveredDatabaseConnection(
    string Role,
    DatabaseType DatabaseType,
    string ConnectionString,
    string DatabaseName,
    string? TenantId,
    string Identity,
    string? SchemaName = null,
    string? TablePrefix = null);

public sealed class UpgradeDatabaseDiscoveryService : IUpgradeDatabaseDiscoveryService
{
    private const string TenantInfoTableName = "boldtc_tenantinfo";

    private readonly IUpgradeCenterConfigurationProvider configurationProvider;
    private readonly IIdpPrivateKeyProvider privateKeyProvider;
    private readonly IBoldConnectionStringDecryptor decryptor;
    private readonly IUpgradeProductContext productContext;
    private readonly ILogger<UpgradeDatabaseDiscoveryService> logger;

    public UpgradeDatabaseDiscoveryService(
        IUpgradeCenterConfigurationProvider configurationProvider,
        IIdpPrivateKeyProvider privateKeyProvider,
        IBoldConnectionStringDecryptor decryptor,
        IUpgradeProductContext productContext,
        ILogger<UpgradeDatabaseDiscoveryService> logger)
    {
        this.configurationProvider = configurationProvider;
        this.privateKeyProvider = privateKeyProvider;
        this.decryptor = decryptor;
        this.productContext = productContext;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveredDatabaseConnection>> DiscoverDatabasesAsync(CancellationToken cancellationToken = default)
    {
        var masterDatabase = await DiscoverMasterDatabaseAsync(cancellationToken);
        var masterDatabaseType = masterDatabase.DatabaseType;
        var masterConnectionString = masterDatabase.ConnectionString;

        var discoveredDatabases = new List<DiscoveredDatabaseConnection>
        {
            masterDatabase
        };

        var tenantInfoTableName = await ResolveTenantInfoTableNameAsync(masterDatabaseType, masterConnectionString, cancellationToken);
        if (string.IsNullOrWhiteSpace(tenantInfoTableName))
        {
            logger.LogWarning(
                "Tenant database table '{TenantInfoTableName}' was not found in the master database {DatabaseName}. Continuing with master database only.",
                TenantInfoTableName,
                masterDatabase.DatabaseName);

            return discoveredDatabases;
        }

        var runtimeConfiguration = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var encryptedPrivateKey = await privateKeyProvider.GetEncryptedPrivateKeyAsync(cancellationToken);
        var tokenKeyMaterial = decryptor.GetTokenKeyMaterial(runtimeConfiguration.MachineKeyDecryptionKey!, encryptedPrivateKey);
        var selectedProduct = productContext.Current;
        var tenantTypeId = selectedProduct.TenantTypeId;
        var tenantRows = await ReadTenantDatabaseRowsAsync(masterDatabaseType, masterConnectionString, tenantInfoTableName, tenantTypeId, cancellationToken);
        foreach (var tenantRow in tenantRows)
        {
            if (string.IsNullOrWhiteSpace(tenantRow.EncryptedConnectionString))
            {
                logger.LogWarning("Tenant database row was skipped because the encrypted connection string is empty. TenantId: {TenantId}.", tenantRow.TenantId ?? "Unknown");
                continue;
            }

            var tenantDatabaseType = MapBoldDatabaseType(tenantRow.DatabaseType);
            var tenantConnectionString = NormalizeDecryptedConnectionString(
                tenantDatabaseType,
                decryptor.DecryptConnectionString(tenantRow.EncryptedConnectionString, tokenKeyMaterial));
            var tenantDatabaseName = GetDatabaseName(tenantDatabaseType, tenantConnectionString);
            discoveredDatabases.Add(CreateDiscoveredDatabase(
                "tenant",
                tenantDatabaseType,
                tenantConnectionString,
                tenantDatabaseName,
                tenantRow.TenantId,
                tenantRow.SchemaName,
                tenantRow.Prefix));
        }

        var uniqueDatabases = GetUniqueDatabases(discoveredDatabases);
        logger.LogInformation(
            "Database discovery completed. Product: {Product}. TenantTypeId: {TenantTypeId}. MasterDatabaseIncluded: {MasterDatabaseIncluded}. TenantRows: {TenantRows}. UniqueDatabases: {UniqueDatabases}. DuplicatesSkipped: {DuplicatesSkipped}.",
            selectedProduct.Key,
            tenantTypeId,
            uniqueDatabases.Any(database => database.Role.Equals("master", StringComparison.OrdinalIgnoreCase)),
            tenantRows.Count,
            uniqueDatabases.Count,
            discoveredDatabases.Count - uniqueDatabases.Count);

        return uniqueDatabases;
    }

    public async Task<DiscoveredDatabaseConnection> DiscoverMasterDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var runtimeConfiguration = await configurationProvider.GetConfigurationAsync(cancellationToken);
        ValidateRuntimeConfiguration(runtimeConfiguration);

        var masterDatabaseType = MapBoldDatabaseType(runtimeConfiguration.MasterDatabaseServerType!.Value);
        var encryptedPrivateKey = await privateKeyProvider.GetEncryptedPrivateKeyAsync(cancellationToken);
        var tokenKeyMaterial = decryptor.GetTokenKeyMaterial(runtimeConfiguration.MachineKeyDecryptionKey!, encryptedPrivateKey);
        var masterConnectionString = NormalizeDecryptedConnectionString(
            masterDatabaseType,
            decryptor.DecryptConnectionString(runtimeConfiguration.EncryptedMasterDatabaseConnectionString!, tokenKeyMaterial));
        var masterDatabaseName = GetDatabaseName(masterDatabaseType, masterConnectionString);

        return CreateDiscoveredDatabase(
            "master",
            masterDatabaseType,
            masterConnectionString,
            masterDatabaseName,
            null,
            runtimeConfiguration.MasterDatabaseSchemaName,
            runtimeConfiguration.MasterDatabaseTablePrefix);
    }

    private static void ValidateRuntimeConfiguration(UpgradeCenterRuntimeConfiguration runtimeConfiguration)
    {
        if (string.IsNullOrWhiteSpace(runtimeConfiguration.EncryptedMasterDatabaseConnectionString))
        {
            throw new InvalidOperationException("Master database connection string is not available from IDP configuration.");
        }

        if (!runtimeConfiguration.MasterDatabaseServerType.HasValue)
        {
            throw new InvalidOperationException("Master database server type is not available from IDP configuration.");
        }

        if (string.IsNullOrWhiteSpace(runtimeConfiguration.MachineKeyDecryptionKey))
        {
            throw new InvalidOperationException("MachineKey decryption key is not available from IDP configuration.");
        }
    }

    private static DatabaseType MapBoldDatabaseType(int value)
    {
        return value switch
        {
            0 => DatabaseType.MSSQL,
            1 => DatabaseType.MySQL,
            2 => throw new NotSupportedException("MSSQLCE database type is not supported for Upgrade Center backup and restore."),
            3 => DatabaseType.Oracle,
            4 => DatabaseType.PostgreSQL,
            _ => throw new NotSupportedException($"Database type value '{value}' is not supported.")
        };
    }

    private static string NormalizeDecryptedConnectionString(DatabaseType databaseType, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Decrypted database connection string is empty.");
        }

        if (databaseType == DatabaseType.PostgreSQL)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                ConnectionIdleLifetime = 30,
                ConnectionPruningInterval = 5
            };
            return builder.ConnectionString;
        }

        return connectionString;
    }

    private async Task<string?> ResolveTenantInfoTableNameAsync(DatabaseType databaseType, string connectionString, CancellationToken cancellationToken)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => await ResolvePostgreSqlTenantInfoTableNameAsync(connectionString, cancellationToken),
            DatabaseType.MSSQL => await ResolveSqlServerTenantInfoTableNameAsync(connectionString, cancellationToken),
            DatabaseType.MySQL => await ResolveMySqlTenantInfoTableNameAsync(connectionString, cancellationToken),
            DatabaseType.Oracle => await ResolveOracleTenantInfoTableNameAsync(connectionString, cancellationToken),
            _ => null
        };
    }

    private async Task<IReadOnlyList<TenantDatabaseRow>> ReadTenantDatabaseRowsAsync(DatabaseType databaseType, string connectionString, string tenantInfoTableName, int tenantTypeId, CancellationToken cancellationToken)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => await ReadPostgreSqlTenantRowsAsync(connectionString, tenantInfoTableName, tenantTypeId, cancellationToken),
            DatabaseType.MSSQL => await ReadSqlServerTenantRowsAsync(connectionString, tenantInfoTableName, tenantTypeId, cancellationToken),
            DatabaseType.MySQL => await ReadMySqlTenantRowsAsync(connectionString, tenantInfoTableName, tenantTypeId, cancellationToken),
            DatabaseType.Oracle => await ReadOracleTenantRowsAsync(connectionString, tenantInfoTableName, tenantTypeId, cancellationToken),
            _ => Array.Empty<TenantDatabaseRow>()
        };
    }

    private static async Task<string?> ResolvePostgreSqlTenantInfoTableNameAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE lower(table_name) = lower(@tableName) LIMIT 1;", connection);
        command.Parameters.AddWithValue("tableName", TenantInfoTableName);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> ResolveSqlServerTenantInfoTableNameAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT name FROM sys.tables WHERE lower(name) = lower(@tableName);", connection);
        command.Parameters.AddWithValue("@tableName", TenantInfoTableName);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> ResolveMySqlTenantInfoTableNameAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND lower(table_name) = lower(@tableName) LIMIT 1;", connection);
        command.Parameters.AddWithValue("@tableName", TenantInfoTableName);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> ResolveOracleTenantInfoTableNameAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OracleCommand("SELECT table_name FROM all_tables WHERE upper(table_name) = upper(:tableName) AND ROWNUM = 1", connection);
        command.Parameters.Add(new OracleParameter("tableName", TenantInfoTableName.ToUpperInvariant()));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<IReadOnlyList<TenantDatabaseRow>> ReadPostgreSqlTenantRowsAsync(string connectionString, string tenantInfoTableName, int tenantTypeId, CancellationToken cancellationToken)
    {
        var query = $"SELECT id, connectionstring, databasetype, schemaname, prefix FROM {QuotePostgreSqlIdentifier(tenantInfoTableName)} WHERE connectionstring IS NOT NULL AND isactive = 1 AND isdeleted = 0 AND tenanttypeid = @tenantTypeId;";
        var rows = new List<TenantDatabaseRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("tenantTypeId", tenantTypeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadTenantRow(reader.GetValue(0), reader.GetValue(1), reader.GetValue(2), reader.GetValue(3), reader.GetValue(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<TenantDatabaseRow>> ReadSqlServerTenantRowsAsync(string connectionString, string tenantInfoTableName, int tenantTypeId, CancellationToken cancellationToken)
    {
        var query = $"SELECT id, connectionstring, databasetype, SchemaName, Prefix FROM {QuoteSqlServerIdentifier(tenantInfoTableName)} WHERE connectionstring IS NOT NULL AND IsActive = 1 AND IsDeleted = 0 AND TenantTypeId = @tenantTypeId;";
        var rows = new List<TenantDatabaseRow>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@tenantTypeId", tenantTypeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadTenantRow(reader.GetValue(0), reader.GetValue(1), reader.GetValue(2), reader.GetValue(3), reader.GetValue(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<TenantDatabaseRow>> ReadMySqlTenantRowsAsync(string connectionString, string tenantInfoTableName, int tenantTypeId, CancellationToken cancellationToken)
    {
        var query = $"SELECT id, connectionstring, databasetype, SchemaName, Prefix FROM {QuoteMySqlIdentifier(tenantInfoTableName)} WHERE connectionstring IS NOT NULL AND IsActive = 1 AND IsDeleted = 0 AND TenantTypeId = @tenantTypeId;";
        var rows = new List<TenantDatabaseRow>();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(query, connection);
        command.Parameters.AddWithValue("@tenantTypeId", tenantTypeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadTenantRow(reader.GetValue(0), reader.GetValue(1), reader.GetValue(2), reader.GetValue(3), reader.GetValue(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<TenantDatabaseRow>> ReadOracleTenantRowsAsync(string connectionString, string tenantInfoTableName, int tenantTypeId, CancellationToken cancellationToken)
    {
        var query = $"SELECT id, connectionstring, databasetype, SchemaName, Prefix FROM {QuoteOracleIdentifier(tenantInfoTableName)} WHERE connectionstring IS NOT NULL AND IsActive = 1 AND IsDeleted = 0 AND TenantTypeId = :tenantTypeId";
        var rows = new List<TenantDatabaseRow>();
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OracleCommand(query, connection);
        command.Parameters.Add(new OracleParameter("tenantTypeId", tenantTypeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadTenantRow(reader.GetValue(0), reader.GetValue(1), reader.GetValue(2), reader.GetValue(3), reader.GetValue(4)));
        }

        return rows;
    }

    private static TenantDatabaseRow ReadTenantRow(object tenantId, object encryptedConnectionString, object databaseType, object schemaName, object prefix)
    {
        return new TenantDatabaseRow(
            tenantId == DBNull.Value ? null : tenantId.ToString(),
            encryptedConnectionString == DBNull.Value ? string.Empty : encryptedConnectionString.ToString() ?? string.Empty,
            Convert.ToInt32(databaseType),
            schemaName == DBNull.Value ? null : schemaName.ToString(),
            prefix == DBNull.Value ? null : prefix.ToString());
    }

    private static DiscoveredDatabaseConnection CreateDiscoveredDatabase(
        string role,
        DatabaseType databaseType,
        string connectionString,
        string databaseName,
        string? tenantId,
        string? schemaName = null,
        string? tablePrefix = null)
    {
        return new DiscoveredDatabaseConnection(
            role,
            databaseType,
            connectionString,
            databaseName,
            tenantId,
            CreateDatabaseIdentity(databaseType, connectionString),
            schemaName,
            tablePrefix);
    }

    private IReadOnlyList<DiscoveredDatabaseConnection> GetUniqueDatabases(IReadOnlyList<DiscoveredDatabaseConnection> databases)
    {
        var uniqueDatabases = new List<DiscoveredDatabaseConnection>();
        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var database in databases)
        {
            if (!seenIdentities.Add(database.Identity))
            {
                logger.LogInformation(
                    "Duplicate discovered database was skipped. DatabaseRole: {DatabaseRole}. DatabaseType: {DatabaseType}. DatabaseName: {DatabaseName}. TenantId: {TenantId}.",
                    database.Role,
                    database.DatabaseType,
                    database.DatabaseName,
                    database.TenantId ?? "None");
                continue;
            }

            uniqueDatabases.Add(database);
        }

        return uniqueDatabases;
    }

    private static string GetDatabaseName(DatabaseType databaseType, string connectionString)
    {
        var databaseName = databaseType switch
        {
            DatabaseType.PostgreSQL => new NpgsqlConnectionStringBuilder(connectionString).Database,
            DatabaseType.MSSQL => new SqlConnectionStringBuilder(connectionString).InitialCatalog,
            DatabaseType.MySQL => new MySqlConnectionStringBuilder(connectionString).Database,
            DatabaseType.Oracle => GetOracleDatabaseName(connectionString),
            _ => "Unknown"
        };

        return string.IsNullOrWhiteSpace(databaseName) ? "Unknown" : databaseName;
    }

    private static string GetOracleDatabaseName(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        return !string.IsNullOrWhiteSpace(builder.UserID) ? builder.UserID : builder.DataSource;
    }

    private static string CreateDatabaseIdentity(DatabaseType databaseType, string connectionString)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => CreatePostgreSqlDatabaseIdentity(connectionString),
            DatabaseType.MSSQL => CreateSqlServerDatabaseIdentity(connectionString),
            DatabaseType.MySQL => CreateMySqlDatabaseIdentity(connectionString),
            DatabaseType.Oracle => CreateOracleDatabaseIdentity(connectionString),
            _ => $"{databaseType}|{connectionString.Trim()}"
        };
    }

    private static string CreatePostgreSqlDatabaseIdentity(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return string.Join('|', DatabaseType.PostgreSQL, NormalizeIdentityPart(builder.Host), builder.Port, NormalizeIdentityPart(builder.Database));
    }

    private static string CreateSqlServerDatabaseIdentity(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return string.Join('|', DatabaseType.MSSQL, NormalizeIdentityPart(builder.DataSource), NormalizeIdentityPart(builder.InitialCatalog));
    }

    private static string CreateMySqlDatabaseIdentity(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return string.Join('|', DatabaseType.MySQL, NormalizeIdentityPart(builder.Server), builder.Port, NormalizeIdentityPart(builder.Database));
    }

    private static string CreateOracleDatabaseIdentity(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        return string.Join('|', DatabaseType.Oracle, NormalizeIdentityPart(builder.DataSource), NormalizeIdentityPart(builder.UserID));
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

    private static string NormalizeIdentityPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private sealed record TenantDatabaseRow(string? TenantId, string EncryptedConnectionString, int DatabaseType, string? SchemaName, string? Prefix);
}
