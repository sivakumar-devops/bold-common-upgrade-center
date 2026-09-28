using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public interface IUpgradeOperationalStateStore
{
    void SaveJob(UpgradeJob job);

    bool TryGetJob(string jobId, out UpgradeJob job);

    bool TryGetLatestActiveJob(out UpgradeJob job);

    bool TryGetLatestJob(out UpgradeJob job);

    void SaveRollback(UpgradeRollbackEntry entry);

    bool TryGetRollback(string id, out UpgradeRollbackEntry entry);

    UpgradeRollbackEntry? GetLatestRollback();
}

public sealed class DatabaseUpgradeOperationalStateStore : IUpgradeOperationalStateStore
{
    private const string TableName = "bolduc_upgradeoperationcontext";
    private const string ContextTypeJob = "job";
    private const string ContextTypeRollback = "rollback";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<DatabaseUpgradeOperationalStateStore> logger;
    private readonly SemaphoreSlim schemaLock = new(1, 1);
    private bool schemaReady;

    public DatabaseUpgradeOperationalStateStore(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseUpgradeOperationalStateStore> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public void SaveJob(UpgradeJob job)
    {
        SaveContext(job.Id, ContextTypeJob, job.Status.ToString(), job);
    }

    public bool TryGetJob(string jobId, out UpgradeJob job)
    {
        job = null!;
        var context = LoadContext(jobId, ContextTypeJob);
        if (context is null)
        {
            return false;
        }

        var parsed = JsonSerializer.Deserialize<UpgradeJob>(context.Payload, JsonOptions);
        if (parsed is null)
        {
            return false;
        }

        job = parsed;
        return true;
    }

    public bool TryGetLatestActiveJob(out UpgradeJob job)
    {
        job = null!;
        var jobs = LoadContexts(ContextTypeJob)
            .Select(context => JsonSerializer.Deserialize<UpgradeJob>(context.Payload, JsonOptions))
            .OfType<UpgradeJob>()
            .Where(IsActive)
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        if (jobs.Length == 0)
        {
            return false;
        }

        job = jobs[0];
        return true;
    }

    public bool TryGetLatestJob(out UpgradeJob job)
    {
        job = null!;
        var jobs = LoadContexts(ContextTypeJob)
            .Select(context => JsonSerializer.Deserialize<UpgradeJob>(context.Payload, JsonOptions))
            .OfType<UpgradeJob>()
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        if (jobs.Length == 0)
        {
            return false;
        }

        job = jobs[0];
        return true;
    }

    public void SaveRollback(UpgradeRollbackEntry entry)
    {
        SaveContext(entry.Id, ContextTypeRollback, ResolveRollbackStatus(entry), SanitizeRollbackEntry(entry));
    }

    public bool TryGetRollback(string id, out UpgradeRollbackEntry entry)
    {
        entry = null!;
        var context = LoadContext(id, ContextTypeRollback);
        if (context is null)
        {
            return false;
        }

        var parsed = JsonSerializer.Deserialize<UpgradeRollbackEntry>(context.Payload, JsonOptions);
        if (parsed is null)
        {
            return false;
        }

        entry = NormalizeRollbackEntry(parsed);
        return true;
    }

    public UpgradeRollbackEntry? GetLatestRollback()
    {
        return LoadContexts(ContextTypeRollback)
            .Select(context => JsonSerializer.Deserialize<UpgradeRollbackEntry>(context.Payload, JsonOptions))
            .OfType<UpgradeRollbackEntry>()
            .Select(NormalizeRollbackEntry)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
    }

    private void SaveContext(string id, string contextType, string status, object payload)
    {
        TryRun(database =>
        {
            EnsureSchema(database);
            var now = DateTimeOffset.UtcNow;
            using var delete = CreateCommand(database, $"DELETE FROM {TableName} WHERE id = @id AND contexttype = @contexttype");
            AddParameter(delete, database.DatabaseType, "id", id);
            AddParameter(delete, database.DatabaseType, "contexttype", contextType);
            delete.ExecuteNonQuery();

            using var insert = CreateCommand(database, $"""
                INSERT INTO {TableName}
                (id, contexttype, status, payload, createdat, modifiedat)
                VALUES
                (@id, @contexttype, @status, @payload, @createdat, @modifiedat)
                """);
            AddParameter(insert, database.DatabaseType, "id", id);
            AddParameter(insert, database.DatabaseType, "contexttype", contextType);
            AddParameter(insert, database.DatabaseType, "status", status);
            AddParameter(insert, database.DatabaseType, "payload", JsonSerializer.Serialize(payload, JsonOptions));
            AddParameter(insert, database.DatabaseType, "createdat", FormatDate(now));
            AddParameter(insert, database.DatabaseType, "modifiedat", FormatDate(now));
            insert.ExecuteNonQuery();
        });
    }

    private PersistedContext? LoadContext(string id, string contextType)
    {
        return TryRun(database =>
        {
            EnsureSchema(database);
            using var command = CreateCommand(database, $"SELECT payload FROM {TableName} WHERE id = @id AND contexttype = @contexttype");
            AddParameter(command, database.DatabaseType, "id", id);
            AddParameter(command, database.DatabaseType, "contexttype", contextType);
            var payload = command.ExecuteScalar()?.ToString();
            return string.IsNullOrWhiteSpace(payload) ? null : new PersistedContext(id, contextType, payload);
        });
    }

    private IReadOnlyList<PersistedContext> LoadContexts(string contextType)
    {
        return TryRun(database =>
        {
            EnsureSchema(database);
            using var command = CreateCommand(database, $"SELECT id, payload FROM {TableName} WHERE contexttype = @contexttype ORDER BY modifiedat DESC");
            AddParameter(command, database.DatabaseType, "contexttype", contextType);
            using var reader = command.ExecuteReader();
            var contexts = new List<PersistedContext>();
            while (reader.Read())
            {
                contexts.Add(new PersistedContext(
                    reader.GetValue(0).ToString() ?? string.Empty,
                    contextType,
                    reader.GetValue(1).ToString() ?? string.Empty));
            }

            return contexts.ToArray();
        }) ?? Array.Empty<PersistedContext>();
    }

    private T? TryRun<T>(Func<OperationalStateConnection, T> action)
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
                    "Upgrade operational context persistence operation timed out after {TimeoutSeconds} seconds.",
                    OperationTimeout.TotalSeconds);
                return default;
            }

            return task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Upgrade operational context persistence operation failed.");
            return default;
        }
    }

    private void TryRun(Action<OperationalStateConnection> action)
    {
        TryRun(database =>
        {
            action(database);
            return true;
        });
    }

    private OperationalStateConnection OpenConnection()
    {
        using var scope = scopeFactory.CreateScope();
        var discoveryService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseDiscoveryService>();
        var master = discoveryService.DiscoverMasterDatabaseAsync().GetAwaiter().GetResult();
        var connection = CreateConnection(master.DatabaseType, master.ConnectionString);
        connection.Open();
        return new OperationalStateConnection(master.DatabaseType, connection);
    }

    private void EnsureSchema(OperationalStateConnection database)
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
                logger.LogInformation("Upgrade Center operational context table was created in the master database.");
            }

            schemaReady = true;
        }
        finally
        {
            schemaLock.Release();
        }
    }

    private static bool TableExists(OperationalStateConnection database)
    {
        var sql = database.DatabaseType switch
        {
            DatabaseType.PostgreSQL => "SELECT 1 FROM information_schema.tables WHERE lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.MSSQL => "SELECT OBJECT_ID(@tableName, 'U')",
            DatabaseType.MySQL => "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.Oracle => "SELECT 1 FROM all_tables WHERE table_name = :tableName AND ROWNUM = 1",
            _ => throw new NotSupportedException($"Database type '{database.DatabaseType}' is not supported for operational context persistence.")
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
                CREATE TABLE bolduc_upgradeoperationcontext (
                    id varchar(128) NOT NULL,
                    contexttype varchar(32) NOT NULL,
                    status varchar(64) NOT NULL,
                    payload text NOT NULL,
                    createdat varchar(40) NOT NULL,
                    modifiedat varchar(40) NOT NULL,
                    PRIMARY KEY (id, contexttype)
                )
                """,
            DatabaseType.MSSQL => """
                CREATE TABLE bolduc_upgradeoperationcontext (
                    id nvarchar(128) NOT NULL,
                    contexttype nvarchar(32) NOT NULL,
                    status nvarchar(64) NOT NULL,
                    payload nvarchar(max) NOT NULL,
                    createdat nvarchar(40) NOT NULL,
                    modifiedat nvarchar(40) NOT NULL,
                    CONSTRAINT pk_bolduc_upgradeoperationcontext PRIMARY KEY (id, contexttype)
                )
                """,
            DatabaseType.MySQL => """
                CREATE TABLE bolduc_upgradeoperationcontext (
                    id varchar(128) NOT NULL,
                    contexttype varchar(32) NOT NULL,
                    status varchar(64) NOT NULL,
                    payload longtext NOT NULL,
                    createdat varchar(40) NOT NULL,
                    modifiedat varchar(40) NOT NULL,
                    PRIMARY KEY (id, contexttype)
                )
                """,
            DatabaseType.Oracle => """
                CREATE TABLE bolduc_upgradeoperationcontext (
                    id varchar2(128) NOT NULL,
                    contexttype varchar2(32) NOT NULL,
                    status varchar2(64) NOT NULL,
                    payload clob NOT NULL,
                    createdat varchar2(40) NOT NULL,
                    modifiedat varchar2(40) NOT NULL,
                    CONSTRAINT pk_bolduc_upgradeoperationcontext PRIMARY KEY (id, contexttype)
                )
                """,
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for operational context persistence.")
        };
    }

    private static UpgradeRollbackEntry SanitizeRollbackEntry(UpgradeRollbackEntry entry)
    {
        return new UpgradeRollbackEntry
        {
            Id = entry.Id,
            CreatedAt = entry.CreatedAt,
            BackupResult = SanitizeBackupResult(entry.BackupResult),
            KubernetesResult = entry.KubernetesResult,
            UpgradeJobId = entry.UpgradeJobId,
            PreviousVersion = entry.PreviousVersion,
            TargetVersion = entry.TargetVersion,
            InitiatedBy = entry.InitiatedBy,
            State = entry.State,
            RestoreStartedAt = entry.RestoreStartedAt,
            RestoreCompletedAt = entry.RestoreCompletedAt,
            RestoreMessage = entry.RestoreMessage,
            LastRestoreAt = entry.LastRestoreAt,
            LastRestoreResult = entry.LastRestoreResult,
            LastKubernetesRollbackResult = entry.LastKubernetesRollbackResult,
            CleanupStartedAt = entry.CleanupStartedAt,
            CleanupCompletedAt = entry.CleanupCompletedAt,
            CleanupMessage = entry.CleanupMessage,
            LastCleanupResult = entry.LastCleanupResult
        };
    }

    private static UpgradeDatabaseBackupResult SanitizeBackupResult(UpgradeDatabaseBackupResult result)
    {
        return result with
        {
            SourceConnectionString = null,
            BackupConnectionString = null,
            DatabaseBackups = result.BackupMappings.Select(mapping => mapping with
            {
                SourceConnectionString = string.Empty,
                BackupConnectionString = string.Empty
            }).ToArray()
        };
    }

    private static string ResolveRollbackStatus(UpgradeRollbackEntry entry)
    {
        if (entry.State != UpgradeRollbackState.Available)
        {
            return entry.State.ToString();
        }

        if (entry.LastRestoreResult is null && entry.LastKubernetesRollbackResult is null)
        {
            return UpgradeRollbackState.Available.ToString();
        }

        return entry.LastRestoreResult?.Succeeded != false && entry.LastKubernetesRollbackResult?.Succeeded != false
            ? UpgradeRollbackState.Consumed.ToString()
            : UpgradeRollbackState.RollbackFailed.ToString();
    }

    private static UpgradeRollbackEntry NormalizeRollbackEntry(UpgradeRollbackEntry entry)
    {
        if (entry.State != UpgradeRollbackState.Available ||
            (entry.LastRestoreResult is null && entry.LastKubernetesRollbackResult is null))
        {
            return entry;
        }

        entry.State = entry.LastRestoreResult?.Succeeded != false && entry.LastKubernetesRollbackResult?.Succeeded != false
            ? UpgradeRollbackState.Consumed
            : UpgradeRollbackState.RollbackFailed;
        entry.RestoreCompletedAt ??= entry.LastRestoreAt;
        entry.RestoreMessage ??= entry.LastRestoreResult?.Message ?? entry.LastKubernetesRollbackResult?.Message;
        return entry;
    }

    private static bool IsActive(UpgradeJob? job)
    {
        return job?.Status is UpgradeJobStatus.Queued or UpgradeJobStatus.Running;
    }

    private static DbConnection CreateConnection(DatabaseType databaseType, string connectionString)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => new NpgsqlConnection(connectionString),
            DatabaseType.MSSQL => new SqlConnection(connectionString),
            DatabaseType.MySQL => new MySqlConnection(connectionString),
            DatabaseType.Oracle => new OracleConnection(connectionString),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for operational context persistence.")
        };
    }

    private static DbCommand CreateCommand(OperationalStateConnection database, string sql)
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

    private static string FormatDate(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O");
    }

    private sealed record PersistedContext(string Id, string ContextType, string Payload);

    private sealed record OperationalStateConnection(DatabaseType DatabaseType, DbConnection Connection) : IDisposable
    {
        public void Dispose()
        {
            Connection.Dispose();
        }
    }
}
