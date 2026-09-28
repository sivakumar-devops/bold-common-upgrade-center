using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public sealed record UpgradeOperationLogEntry(
    long Sequence,
    string JobId,
    string? Stage,
    string Level,
    string Source,
    string Message,
    DateTimeOffset CreatedAt);

public interface IUpgradeOperationLogStore
{
    void Append(string jobId, string? stage, string level, string source, string message);

    IReadOnlyList<UpgradeOperationLogEntry> List(string jobId, int take = 500);

    IReadOnlyList<UpgradeOperationLogEntry> ListAfter(string jobId, long afterSequence, int take = 200);

    IReadOnlyList<UpgradeOperationLogEntry> ListAll(string jobId);

    void RetainLatestJobs(int jobCount);
}

public sealed class DatabaseUpgradeOperationLogStore : IUpgradeOperationLogStore
{
    private const string TableName = "bolduc_upgradeoperationlog";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<DatabaseUpgradeOperationLogStore> logger;
    private readonly SemaphoreSlim schemaLock = new(1, 1);
    private readonly SemaphoreSlim appendLock = new(1, 1);
    private bool schemaReady;

    public DatabaseUpgradeOperationLogStore(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseUpgradeOperationLogStore> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public void Append(string jobId, string? stage, string level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        TryRun(database =>
        {
            EnsureSchema(database);
            appendLock.Wait();
            try
            {
                var safeStage = string.IsNullOrWhiteSpace(stage) ? null : stage.Trim();
                var safeLevel = Truncate(level, 16);
                var safeSource = Truncate(source, 64);
                if (HasDuplicateEntry(database, jobId, safeStage, safeLevel, safeSource, message))
                {
                    return;
                }

                var sequence = GetNextSequence(database, jobId);
                using var command = CreateCommand(database, $"""
                    INSERT INTO {TableName}
                        (jobid, sequenceid, stage, loglevel, source, message, createdat)
                    VALUES
                        (@jobid, @sequenceid, @stage, @loglevel, @source, @message, @createdat)
                    """);

                AddParameter(command, database.DatabaseType, "jobid", jobId);
                AddParameter(command, database.DatabaseType, "sequenceid", sequence);
                AddParameter(command, database.DatabaseType, "stage", safeStage is null ? DBNull.Value : safeStage);
                AddParameter(command, database.DatabaseType, "loglevel", safeLevel);
                AddParameter(command, database.DatabaseType, "source", safeSource);
                AddParameter(command, database.DatabaseType, "message", message);
                AddParameter(command, database.DatabaseType, "createdat", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
            finally
            {
                appendLock.Release();
            }
        });
    }

    public IReadOnlyList<UpgradeOperationLogEntry> List(string jobId, int take = 500)
    {
        return TryRun(database =>
        {
            EnsureSchema(database);
            var limit = Math.Clamp(take, 1, 2000);
            var sql = database.DatabaseType switch
            {
                DatabaseType.MSSQL => $"SELECT * FROM (SELECT TOP ({limit}) jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid ORDER BY sequenceid DESC) logs ORDER BY sequenceid ASC",
                DatabaseType.Oracle => $"SELECT jobid, sequenceid, stage, loglevel, source, message, createdat FROM (SELECT jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid ORDER BY sequenceid DESC) WHERE ROWNUM <= {limit} ORDER BY sequenceid ASC",
                _ => $"SELECT jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid ORDER BY sequenceid ASC LIMIT {limit}"
            };

            using var command = CreateCommand(database, sql);
            AddParameter(command, database.DatabaseType, "jobid", jobId);
            return ReadEntries(command);
        }) ?? Array.Empty<UpgradeOperationLogEntry>();
    }

    public IReadOnlyList<UpgradeOperationLogEntry> ListAfter(string jobId, long afterSequence, int take = 200)
    {
        return TryRun(database =>
        {
            EnsureSchema(database);
            var limit = Math.Clamp(take, 1, 1000);
            var sql = database.DatabaseType switch
            {
                DatabaseType.MSSQL => $"SELECT TOP ({limit}) jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid AND sequenceid > @sequenceid ORDER BY sequenceid ASC",
                DatabaseType.Oracle => $"SELECT jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid AND sequenceid > @sequenceid ORDER BY sequenceid ASC FETCH FIRST {limit} ROWS ONLY",
                _ => $"SELECT jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid AND sequenceid > @sequenceid ORDER BY sequenceid ASC LIMIT {limit}"
            };

            using var command = CreateCommand(database, sql);
            AddParameter(command, database.DatabaseType, "jobid", jobId);
            AddParameter(command, database.DatabaseType, "sequenceid", afterSequence);
            return ReadEntries(command);
        }) ?? Array.Empty<UpgradeOperationLogEntry>();
    }

    public IReadOnlyList<UpgradeOperationLogEntry> ListAll(string jobId)
    {
        return TryRun(database =>
        {
            EnsureSchema(database);
            using var command = CreateCommand(database, $"SELECT jobid, sequenceid, stage, loglevel, source, message, createdat FROM {TableName} WHERE jobid = @jobid ORDER BY sequenceid ASC");
            AddParameter(command, database.DatabaseType, "jobid", jobId);
            return ReadEntries(command);
        }) ?? Array.Empty<UpgradeOperationLogEntry>();
    }

    public void RetainLatestJobs(int jobCount)
    {
        if (jobCount < 1)
        {
            return;
        }

        TryRun(database =>
        {
            EnsureSchema(database);
            using var selectCommand = CreateCommand(database, $"SELECT jobid, MAX(createdat) AS lastcreatedat FROM {TableName} GROUP BY jobid ORDER BY MAX(createdat) DESC");
            var jobIdsToDelete = new List<string>();
            using (var reader = selectCommand.ExecuteReader())
            {
                var index = 0;
                while (reader.Read())
                {
                    var jobId = reader.GetValue(0).ToString();
                    if (!string.IsNullOrWhiteSpace(jobId) && index++ >= jobCount)
                    {
                        jobIdsToDelete.Add(jobId);
                    }
                }
            }

            foreach (var jobId in jobIdsToDelete)
            {
                using var deleteCommand = CreateCommand(database, $"DELETE FROM {TableName} WHERE jobid = @jobid");
                AddParameter(deleteCommand, database.DatabaseType, "jobid", jobId);
                deleteCommand.ExecuteNonQuery();
            }
        });
    }

    private T? TryRun<T>(Func<OperationLogConnection, T> action)
    {
        try
        {
            var task = Task.Run(() =>
            {
                using var database = OpenConnection();
                return action(database);
            });

            if (!task.Wait(OperationTimeout))
            {
                logger.LogWarning(
                    "Upgrade operation log persistence operation timed out after {TimeoutSeconds} seconds.",
                    OperationTimeout.TotalSeconds);
                return default;
            }

            return task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Upgrade operation log persistence operation failed.");
            return default;
        }
    }

    private void TryRun(Action<OperationLogConnection> action)
    {
        TryRun(database =>
        {
            action(database);
            return true;
        });
    }

    private OperationLogConnection OpenConnection()
    {
        using var scope = scopeFactory.CreateScope();
        var discoveryService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseDiscoveryService>();
        var master = discoveryService.DiscoverMasterDatabaseAsync().GetAwaiter().GetResult();
        var connection = CreateConnection(master.DatabaseType, master.ConnectionString);
        connection.Open();
        return new OperationLogConnection(master.DatabaseType, connection);
    }

    private void EnsureSchema(OperationLogConnection database)
    {
        if (schemaReady)
        {
            return;
        }

        schemaLock.Wait();
        try
        {
            if (schemaReady)
            {
                return;
            }

            if (!TableExists(database))
            {
                using var command = CreateCommand(database, CreateTableSql(database.DatabaseType));
                command.ExecuteNonQuery();
                logger.LogInformation("Upgrade Center operation log table was created in the master database.");
            }

            schemaReady = true;
        }
        finally
        {
            schemaLock.Release();
        }
    }

    private static bool TableExists(OperationLogConnection database)
    {
        var sql = database.DatabaseType switch
        {
            DatabaseType.PostgreSQL => "SELECT 1 FROM information_schema.tables WHERE lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.MSSQL => "SELECT OBJECT_ID(@tableName, 'U')",
            DatabaseType.MySQL => "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.Oracle => "SELECT 1 FROM all_tables WHERE table_name = :tableName AND ROWNUM = 1",
            _ => throw new NotSupportedException($"Database type '{database.DatabaseType}' is not supported for operation log persistence.")
        };

        using var command = CreateCommand(database, sql);
        AddParameter(command, database.DatabaseType, "tableName", database.DatabaseType == DatabaseType.Oracle ? TableName.ToUpperInvariant() : TableName);
        var result = command.ExecuteScalar();
        return result is not null && result != DBNull.Value;
    }

    private static string CreateTableSql(DatabaseType databaseType)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => """
                CREATE TABLE bolduc_upgradeoperationlog (
                    jobid varchar(128) NOT NULL,
                    sequenceid bigint NOT NULL,
                    stage varchar(64) NULL,
                    loglevel varchar(16) NOT NULL,
                    source varchar(64) NOT NULL,
                    message text NOT NULL,
                    createdat varchar(40) NOT NULL,
                    PRIMARY KEY (jobid, sequenceid)
                )
                """,
            DatabaseType.MSSQL => """
                CREATE TABLE bolduc_upgradeoperationlog (
                    jobid nvarchar(128) NOT NULL,
                    sequenceid bigint NOT NULL,
                    stage nvarchar(64) NULL,
                    loglevel nvarchar(16) NOT NULL,
                    source nvarchar(64) NOT NULL,
                    message nvarchar(max) NOT NULL,
                    createdat nvarchar(40) NOT NULL,
                    CONSTRAINT pk_bolduc_upgradeoperationlog PRIMARY KEY (jobid, sequenceid)
                )
                """,
            DatabaseType.MySQL => """
                CREATE TABLE bolduc_upgradeoperationlog (
                    jobid varchar(128) NOT NULL,
                    sequenceid bigint NOT NULL,
                    stage varchar(64) NULL,
                    loglevel varchar(16) NOT NULL,
                    source varchar(64) NOT NULL,
                    message longtext NOT NULL,
                    createdat varchar(40) NOT NULL,
                    PRIMARY KEY (jobid, sequenceid)
                )
                """,
            DatabaseType.Oracle => """
                CREATE TABLE bolduc_upgradeoperationlog (
                    jobid varchar2(128) NOT NULL,
                    sequenceid number(19) NOT NULL,
                    stage varchar2(64) NULL,
                    loglevel varchar2(16) NOT NULL,
                    source varchar2(64) NOT NULL,
                    message clob NOT NULL,
                    createdat varchar2(40) NOT NULL,
                    CONSTRAINT pk_bolduc_upgradeoperationlog PRIMARY KEY (jobid, sequenceid)
                )
                """,
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for operation log persistence.")
        };
    }

    private static long GetNextSequence(OperationLogConnection database, string jobId)
    {
        using var command = CreateCommand(database, $"SELECT MAX(sequenceid) FROM {TableName} WHERE jobid = @jobid");
        AddParameter(command, database.DatabaseType, "jobid", jobId);
        var value = command.ExecuteScalar();
        return value is null || value == DBNull.Value
            ? 1
            : Convert.ToInt64(value) + 1;
    }

    private static bool HasDuplicateEntry(
        OperationLogConnection database,
        string jobId,
        string? stage,
        string level,
        string source,
        string message)
    {
        var stageFilter = stage is null ? "stage IS NULL" : "stage = @stage";
        var sql = database.DatabaseType switch
        {
            DatabaseType.MSSQL => $"SELECT TOP (100) message FROM {TableName} WHERE jobid = @jobid AND loglevel = @loglevel AND source = @source AND {stageFilter} ORDER BY sequenceid DESC",
            DatabaseType.Oracle => $"SELECT message FROM {TableName} WHERE jobid = @jobid AND loglevel = @loglevel AND source = @source AND {stageFilter} ORDER BY sequenceid DESC FETCH FIRST 100 ROWS ONLY",
            _ => $"SELECT message FROM {TableName} WHERE jobid = @jobid AND loglevel = @loglevel AND source = @source AND {stageFilter} ORDER BY sequenceid DESC LIMIT 100"
        };

        using var command = CreateCommand(database, sql);
        AddParameter(command, database.DatabaseType, "jobid", jobId);
        AddParameter(command, database.DatabaseType, "loglevel", level);
        AddParameter(command, database.DatabaseType, "source", source);
        if (stage is not null)
        {
            AddParameter(command, database.DatabaseType, "stage", stage);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var existingMessage = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString() ?? string.Empty;
            if (string.Equals(existingMessage, message, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<UpgradeOperationLogEntry> ReadEntries(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var entries = new List<UpgradeOperationLogEntry>();
        while (reader.Read())
        {
            var createdAtValue = reader.GetValue(6)?.ToString();
            _ = DateTimeOffset.TryParse(createdAtValue, out var createdAt);
            entries.Add(new UpgradeOperationLogEntry(
                Convert.ToInt64(reader.GetValue(1)),
                reader.GetValue(0).ToString() ?? string.Empty,
                reader.IsDBNull(2) ? null : reader.GetValue(2).ToString(),
                reader.GetValue(3).ToString() ?? "Info",
                reader.GetValue(4).ToString() ?? "UpgradeCenter",
                reader.GetValue(5).ToString() ?? string.Empty,
                createdAt == default ? DateTimeOffset.UtcNow : createdAt));
        }

        return entries;
    }

    private static DbConnection CreateConnection(DatabaseType databaseType, string connectionString)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => new NpgsqlConnection(connectionString),
            DatabaseType.MSSQL => new SqlConnection(connectionString),
            DatabaseType.MySQL => new MySqlConnection(connectionString),
            DatabaseType.Oracle => new OracleConnection(connectionString),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for operation log persistence.")
        };
    }

    private static DbCommand CreateCommand(OperationLogConnection database, string sql)
    {
        var command = database.Connection.CreateCommand();
        command.CommandText = database.DatabaseType == DatabaseType.Oracle
            ? sql.Replace("@", ":", StringComparison.Ordinal)
            : sql;
        return command;
    }

    private static void AddParameter(DbCommand command, DatabaseType databaseType, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = databaseType == DatabaseType.Oracle ? $":{name}" : $"@{name}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed record OperationLogConnection(DatabaseType DatabaseType, DbConnection Connection) : IDisposable
    {
        public void Dispose()
        {
            Connection.Dispose();
        }
    }
}
