using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public sealed class DatabaseUpgradeHistoryStore : IUpgradeHistoryStore
{
    private const string TableName = "bolduc_upgradeoperationhistory";
    private const string ProductColumnName = "product";
    private readonly IUpgradeDatabaseDiscoveryService databaseDiscoveryService;
    private readonly ILogger<DatabaseUpgradeHistoryStore> logger;
    private readonly SemaphoreSlim schemaLock = new(1, 1);
    private bool schemaReady;

    public DatabaseUpgradeHistoryStore(
        IUpgradeDatabaseDiscoveryService databaseDiscoveryService,
        ILogger<DatabaseUpgradeHistoryStore> logger)
    {
        this.databaseDiscoveryService = databaseDiscoveryService;
        this.logger = logger;
    }

    public async Task SaveStartedAsync(UpgradeHistoryRecord record, CancellationToken cancellationToken = default)
    {
        var master = await OpenMasterConnectionAsync(cancellationToken);
        await using var connection = master.Connection;
        await EnsureSchemaAsync(master, cancellationToken);

        const string sql = """
            INSERT INTO bolduc_upgradeoperationhistory
            (id, jobid, parentjobid, product, operationtype, previousversion, targetversion, upgradetype, status, startedat, completedat,
             initiatedbyuserid, initiatedbyname, initiatedbyemail, initiatedbyrole, rollbackmode, rollbackjobid, rollbackavailable,
             failurestage, failuresummary, createdat, modifiedat)
            VALUES
            (@id, @jobid, @parentjobid, @product, @operationtype, @previousversion, @targetversion, @upgradetype, @status, @startedat, @completedat,
             @initiatedbyuserid, @initiatedbyname, @initiatedbyemail, @initiatedbyrole, @rollbackmode, @rollbackjobid, @rollbackavailable,
             @failurestage, @failuresummary, @createdat, @modifiedat)
            """;

        await using var command = CreateCommand(master, sql);
        AddCommonParameters(command, master.DatabaseType, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateCompletionAsync(
        string jobId,
        string status,
        DateTimeOffset? completedAt,
        bool rollbackAvailable,
        string? rollbackJobId,
        string? failureStage,
        string? failureSummary,
        CancellationToken cancellationToken = default)
    {
        var master = await OpenMasterConnectionAsync(cancellationToken);
        await using var connection = master.Connection;
        await EnsureSchemaAsync(master, cancellationToken);

        const string sql = """
            UPDATE bolduc_upgradeoperationhistory
            SET status = @status,
                completedat = @completedat,
                rollbackavailable = @rollbackavailable,
                rollbackjobid = @rollbackjobid,
                failurestage = @failurestage,
                failuresummary = @failuresummary,
                modifiedat = @modifiedat
            WHERE jobid = @jobid
            """;

        await using var command = CreateCommand(master, sql);
        AddParameter(command, master.DatabaseType, "status", status);
        AddParameter(command, master.DatabaseType, "completedat", FormatDate(completedAt));
        AddParameter(command, master.DatabaseType, "rollbackavailable", rollbackAvailable ? 1 : 0);
        AddParameter(command, master.DatabaseType, "rollbackjobid", rollbackJobId);
        AddParameter(command, master.DatabaseType, "failurestage", failureStage);
        AddParameter(command, master.DatabaseType, "failuresummary", Truncate(failureSummary, 2000));
        AddParameter(command, master.DatabaseType, "modifiedat", FormatDate(DateTimeOffset.UtcNow));
        AddParameter(command, master.DatabaseType, "jobid", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateStatusAsync(
        string jobId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var master = await OpenMasterConnectionAsync(cancellationToken);
        await using var connection = master.Connection;
        await EnsureSchemaAsync(master, cancellationToken);

        const string sql = """
            UPDATE bolduc_upgradeoperationhistory
            SET status = @status,
                modifiedat = @modifiedat
            WHERE jobid = @jobid
            """;

        await using var command = CreateCommand(master, sql);
        AddParameter(command, master.DatabaseType, "status", status);
        AddParameter(command, master.DatabaseType, "modifiedat", FormatDate(DateTimeOffset.UtcNow));
        AddParameter(command, master.DatabaseType, "jobid", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UpgradeHistoryRecord>> ListAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        var master = await OpenMasterConnectionAsync(cancellationToken);
        await using var connection = master.Connection;
        await EnsureSchemaAsync(master, cancellationToken);

        var limit = Math.Clamp(take, 1, 200);
        var sql = master.DatabaseType switch
        {
            DatabaseType.MSSQL => $"SELECT TOP ({limit}) * FROM {TableName} ORDER BY startedat DESC, createdat DESC",
            DatabaseType.Oracle => $"SELECT * FROM {TableName} ORDER BY startedat DESC, createdat DESC FETCH FIRST {limit} ROWS ONLY",
            _ => $"SELECT * FROM {TableName} ORDER BY startedat DESC, createdat DESC LIMIT {limit}"
        };

        await using var command = CreateCommand(master, sql);
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<UpgradeHistoryRecord?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var master = await OpenMasterConnectionAsync(cancellationToken);
        await using var connection = master.Connection;
        await EnsureSchemaAsync(master, cancellationToken);

        const string sql = "SELECT * FROM bolduc_upgradeoperationhistory WHERE jobid = @jobid";
        await using var command = CreateCommand(master, sql);
        AddParameter(command, master.DatabaseType, "jobid", jobId);
        return (await ReadRecordsAsync(command, cancellationToken)).FirstOrDefault();
    }

    private async Task<MasterHistoryConnection> OpenMasterConnectionAsync(CancellationToken cancellationToken)
    {
        var master = await databaseDiscoveryService.DiscoverMasterDatabaseAsync(cancellationToken);
        var connection = CreateConnection(master.DatabaseType, master.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return new MasterHistoryConnection(master.DatabaseType, connection);
    }

    private async Task EnsureSchemaAsync(MasterHistoryConnection master, CancellationToken cancellationToken)
    {
        if (schemaReady)
        {
            return;
        }

        await schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (schemaReady)
            {
                return;
            }

            if (!await TableExistsAsync(master, cancellationToken))
            {
                await using var command = CreateCommand(master, CreateTableSql(master.DatabaseType));
                await command.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("Upgrade Center history table was created in the master database.");
            }
            else if (!await ColumnExistsAsync(master, ProductColumnName, cancellationToken))
            {
                await using var command = CreateCommand(master, AddProductColumnSql(master.DatabaseType));
                await command.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("Upgrade Center history table was updated with product tracking.");
            }

            schemaReady = true;
        }
        finally
        {
            schemaLock.Release();
        }
    }

    private static async Task<bool> TableExistsAsync(MasterHistoryConnection master, CancellationToken cancellationToken)
    {
        var sql = master.DatabaseType switch
        {
            DatabaseType.PostgreSQL => "SELECT 1 FROM information_schema.tables WHERE lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.MSSQL => "SELECT OBJECT_ID(@tableName, 'U')",
            DatabaseType.MySQL => "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.Oracle => "SELECT 1 FROM all_tables WHERE table_name = :tableName AND ROWNUM = 1",
            _ => throw new NotSupportedException($"Database type '{master.DatabaseType}' is not supported for upgrade history.")
        };

        await using var command = CreateCommand(master, sql);
        AddParameter(command, master.DatabaseType, "tableName", master.DatabaseType == DatabaseType.Oracle ? TableName.ToUpperInvariant() : TableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    private static async Task<bool> ColumnExistsAsync(MasterHistoryConnection master, string columnName, CancellationToken cancellationToken)
    {
        var sql = master.DatabaseType switch
        {
            DatabaseType.PostgreSQL => "SELECT 1 FROM information_schema.columns WHERE lower(table_name) = lower(@tableName) AND lower(column_name) = lower(@columnName) LIMIT 1",
            DatabaseType.MSSQL => "SELECT COL_LENGTH(@tableName, @columnName)",
            DatabaseType.MySQL => "SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND lower(table_name) = lower(@tableName) AND lower(column_name) = lower(@columnName) LIMIT 1",
            DatabaseType.Oracle => "SELECT 1 FROM all_tab_columns WHERE table_name = :tableName AND column_name = :columnName AND ROWNUM = 1",
            _ => throw new NotSupportedException($"Database type '{master.DatabaseType}' is not supported for upgrade history.")
        };

        await using var command = CreateCommand(master, sql);
        AddParameter(command, master.DatabaseType, "tableName", master.DatabaseType == DatabaseType.Oracle ? TableName.ToUpperInvariant() : TableName);
        AddParameter(command, master.DatabaseType, "columnName", master.DatabaseType == DatabaseType.Oracle ? columnName.ToUpperInvariant() : columnName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    private static string AddProductColumnSql(DatabaseType databaseType)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => $"ALTER TABLE {TableName} ADD COLUMN {ProductColumnName} varchar(64) NULL",
            DatabaseType.MSSQL => $"ALTER TABLE {TableName} ADD {ProductColumnName} nvarchar(64) NULL",
            DatabaseType.MySQL => $"ALTER TABLE {TableName} ADD COLUMN {ProductColumnName} varchar(64) NULL",
            DatabaseType.Oracle => $"ALTER TABLE {TableName} ADD ({ProductColumnName} varchar2(64) NULL)",
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for upgrade history.")
        };
    }

    private static string CreateTableSql(DatabaseType databaseType)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => """
                CREATE TABLE bolduc_upgradeoperationhistory (
                    id varchar(64) PRIMARY KEY,
                    jobid varchar(128) NOT NULL UNIQUE,
                    parentjobid varchar(128) NULL,
                    product varchar(64) NULL,
                    operationtype varchar(64) NOT NULL,
                    previousversion varchar(64) NULL,
                    targetversion varchar(64) NULL,
                    upgradetype varchar(64) NULL,
                    status varchar(64) NOT NULL,
                    startedat varchar(40) NOT NULL,
                    completedat varchar(40) NULL,
                    initiatedbyuserid varchar(128) NOT NULL,
                    initiatedbyname varchar(256) NOT NULL,
                    initiatedbyemail varchar(256) NULL,
                    initiatedbyrole varchar(128) NULL,
                    rollbackmode varchar(64) NULL,
                    rollbackjobid varchar(128) NULL,
                    rollbackavailable integer NOT NULL DEFAULT 0,
                    failurestage varchar(128) NULL,
                    failuresummary text NULL,
                    createdat varchar(40) NOT NULL,
                    modifiedat varchar(40) NOT NULL
                )
                """,
            DatabaseType.MSSQL => """
                CREATE TABLE bolduc_upgradeoperationhistory (
                    id nvarchar(64) NOT NULL PRIMARY KEY,
                    jobid nvarchar(128) NOT NULL UNIQUE,
                    parentjobid nvarchar(128) NULL,
                    product nvarchar(64) NULL,
                    operationtype nvarchar(64) NOT NULL,
                    previousversion nvarchar(64) NULL,
                    targetversion nvarchar(64) NULL,
                    upgradetype nvarchar(64) NULL,
                    status nvarchar(64) NOT NULL,
                    startedat nvarchar(40) NOT NULL,
                    completedat nvarchar(40) NULL,
                    initiatedbyuserid nvarchar(128) NOT NULL,
                    initiatedbyname nvarchar(256) NOT NULL,
                    initiatedbyemail nvarchar(256) NULL,
                    initiatedbyrole nvarchar(128) NULL,
                    rollbackmode nvarchar(64) NULL,
                    rollbackjobid nvarchar(128) NULL,
                    rollbackavailable int NOT NULL DEFAULT 0,
                    failurestage nvarchar(128) NULL,
                    failuresummary nvarchar(2000) NULL,
                    createdat nvarchar(40) NOT NULL,
                    modifiedat nvarchar(40) NOT NULL
                )
                """,
            DatabaseType.MySQL => """
                CREATE TABLE bolduc_upgradeoperationhistory (
                    id varchar(64) NOT NULL PRIMARY KEY,
                    jobid varchar(128) NOT NULL UNIQUE,
                    parentjobid varchar(128) NULL,
                    product varchar(64) NULL,
                    operationtype varchar(64) NOT NULL,
                    previousversion varchar(64) NULL,
                    targetversion varchar(64) NULL,
                    upgradetype varchar(64) NULL,
                    status varchar(64) NOT NULL,
                    startedat varchar(40) NOT NULL,
                    completedat varchar(40) NULL,
                    initiatedbyuserid varchar(128) NOT NULL,
                    initiatedbyname varchar(256) NOT NULL,
                    initiatedbyemail varchar(256) NULL,
                    initiatedbyrole varchar(128) NULL,
                    rollbackmode varchar(64) NULL,
                    rollbackjobid varchar(128) NULL,
                    rollbackavailable int NOT NULL DEFAULT 0,
                    failurestage varchar(128) NULL,
                    failuresummary text NULL,
                    createdat varchar(40) NOT NULL,
                    modifiedat varchar(40) NOT NULL
                )
                """,
            DatabaseType.Oracle => """
                CREATE TABLE bolduc_upgradeoperationhistory (
                    id varchar2(64) NOT NULL PRIMARY KEY,
                    jobid varchar2(128) NOT NULL UNIQUE,
                    parentjobid varchar2(128) NULL,
                    product varchar2(64) NULL,
                    operationtype varchar2(64) NOT NULL,
                    previousversion varchar2(64) NULL,
                    targetversion varchar2(64) NULL,
                    upgradetype varchar2(64) NULL,
                    status varchar2(64) NOT NULL,
                    startedat varchar2(40) NOT NULL,
                    completedat varchar2(40) NULL,
                    initiatedbyuserid varchar2(128) NOT NULL,
                    initiatedbyname varchar2(256) NOT NULL,
                    initiatedbyemail varchar2(256) NULL,
                    initiatedbyrole varchar2(128) NULL,
                    rollbackmode varchar2(64) NULL,
                    rollbackjobid varchar2(128) NULL,
                    rollbackavailable number(1) DEFAULT 0 NOT NULL,
                    failurestage varchar2(128) NULL,
                    failuresummary clob NULL,
                    createdat varchar2(40) NOT NULL,
                    modifiedat varchar2(40) NOT NULL
                )
                """,
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for upgrade history.")
        };
    }

    private static DbConnection CreateConnection(DatabaseType databaseType, string connectionString)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => new NpgsqlConnection(connectionString),
            DatabaseType.MSSQL => new SqlConnection(connectionString),
            DatabaseType.MySQL => new MySqlConnection(connectionString),
            DatabaseType.Oracle => new OracleConnection(connectionString),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for upgrade history.")
        };
    }

    private static DbCommand CreateCommand(MasterHistoryConnection master, string sql)
    {
        var command = master.Connection.CreateCommand();
        command.CommandText = master.DatabaseType == DatabaseType.Oracle
            ? sql.Replace("@", ":", StringComparison.Ordinal)
            : sql;
        return command;
    }

    private static void AddCommonParameters(DbCommand command, DatabaseType databaseType, UpgradeHistoryRecord record)
    {
        AddParameter(command, databaseType, "id", record.Id);
        AddParameter(command, databaseType, "jobid", record.JobId);
        AddParameter(command, databaseType, "parentjobid", record.ParentJobId);
        AddParameter(command, databaseType, "product", Truncate(record.Product, 64));
        AddParameter(command, databaseType, "operationtype", record.OperationType);
        AddParameter(command, databaseType, "previousversion", record.PreviousVersion);
        AddParameter(command, databaseType, "targetversion", record.TargetVersion);
        AddParameter(command, databaseType, "upgradetype", record.UpgradeType);
        AddParameter(command, databaseType, "status", record.Status);
        AddParameter(command, databaseType, "startedat", FormatDate(record.StartedAt));
        AddParameter(command, databaseType, "completedat", FormatDate(record.CompletedAt));
        AddParameter(command, databaseType, "initiatedbyuserid", record.InitiatedByUserId);
        AddParameter(command, databaseType, "initiatedbyname", record.InitiatedByName);
        AddParameter(command, databaseType, "initiatedbyemail", record.InitiatedByEmail);
        AddParameter(command, databaseType, "initiatedbyrole", record.InitiatedByRole);
        AddParameter(command, databaseType, "rollbackmode", record.RollbackMode);
        AddParameter(command, databaseType, "rollbackjobid", record.RollbackJobId);
        AddParameter(command, databaseType, "rollbackavailable", record.RollbackAvailable ? 1 : 0);
        AddParameter(command, databaseType, "failurestage", record.FailureStage);
        AddParameter(command, databaseType, "failuresummary", Truncate(record.FailureSummary, 2000));
        AddParameter(command, databaseType, "createdat", FormatDate(record.CreatedAt));
        AddParameter(command, databaseType, "modifiedat", FormatDate(record.ModifiedAt));
    }

    private static void AddParameter(DbCommand command, DatabaseType databaseType, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = databaseType == DatabaseType.Oracle ? $":{name}" : $"@{name}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task<IReadOnlyList<UpgradeHistoryRecord>> ReadRecordsAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var records = new List<UpgradeHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new UpgradeHistoryRecord
            {
                Id = ReadString(reader, "id") ?? string.Empty,
                JobId = ReadString(reader, "jobid") ?? string.Empty,
                ParentJobId = ReadString(reader, "parentjobid"),
                Product = ReadString(reader, "product") ?? "BI",
                OperationType = ReadString(reader, "operationtype") ?? string.Empty,
                PreviousVersion = ReadString(reader, "previousversion"),
                TargetVersion = ReadString(reader, "targetversion"),
                UpgradeType = ReadString(reader, "upgradetype"),
                Status = ReadString(reader, "status") ?? string.Empty,
                StartedAt = ParseDate(ReadString(reader, "startedat")) ?? DateTimeOffset.UtcNow,
                CompletedAt = ParseDate(ReadString(reader, "completedat")),
                InitiatedByUserId = ReadString(reader, "initiatedbyuserid") ?? string.Empty,
                InitiatedByName = ReadString(reader, "initiatedbyname") ?? string.Empty,
                InitiatedByEmail = ReadString(reader, "initiatedbyemail"),
                InitiatedByRole = ReadString(reader, "initiatedbyrole"),
                RollbackMode = ReadString(reader, "rollbackmode"),
                RollbackJobId = ReadString(reader, "rollbackjobid"),
                RollbackAvailable = ReadInt(reader, "rollbackavailable") == 1,
                FailureStage = ReadString(reader, "failurestage"),
                FailureSummary = ReadString(reader, "failuresummary"),
                CreatedAt = ParseDate(ReadString(reader, "createdat")) ?? DateTimeOffset.UtcNow,
                ModifiedAt = ParseDate(ReadString(reader, "modifiedat")) ?? DateTimeOffset.UtcNow
            });
        }

        return records;
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = GetOrdinal(reader, name);
        return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal).ToString();
    }

    private static int ReadInt(DbDataReader reader, string name)
    {
        var ordinal = GetOrdinal(reader, name);
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        return Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static int GetOrdinal(DbDataReader reader, string name)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return reader.GetOrdinal(name);
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O");
    }

    private static string? FormatDate(DateTimeOffset? value)
    {
        return value.HasValue ? FormatDate(value.Value) : null;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record MasterHistoryConnection(DatabaseType DatabaseType, DbConnection Connection);
}
