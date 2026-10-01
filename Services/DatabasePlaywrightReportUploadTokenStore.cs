using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public sealed class DatabasePlaywrightReportUploadTokenStore : IPlaywrightReportUploadTokenStore
{
    private const string TableName = "bolduc_playwrightuploadtoken";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<DatabasePlaywrightReportUploadTokenStore> logger;
    private readonly SemaphoreSlim schemaLock = new(1, 1);
    private readonly object syncRoot = new();
    private bool schemaReady;

    public DatabasePlaywrightReportUploadTokenStore(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabasePlaywrightReportUploadTokenStore> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public string Register(string jobId, PlaywrightValidationMode mode, TimeSpan ttl, string? existingToken = null)
    {
        var token = string.IsNullOrWhiteSpace(existingToken)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : existingToken.Trim();
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : ttl);
        var normalizedJobId = NormalizeJobId(jobId);
        var stage = ToStage(mode);
        var tokenHash = ComputeSha256Hash(token);

        lock (syncRoot)
        {
            TryRun(database =>
            {
                EnsureSchema(database);
                PruneExpired(database);

                if (TokenEntryExists(database, normalizedJobId, stage))
                {
                    using var update = CreateCommand(database, $"""
                        UPDATE {TableName}
                        SET tokenhash = @tokenhash, expiresat = @expiresat, modifiedat = @modifiedat
                        WHERE jobid = @jobid AND validationstage = @validationstage
                        """);
                    AddParameter(update, database.DatabaseType, "tokenhash", tokenHash);
                    AddParameter(update, database.DatabaseType, "expiresat", FormatDate(expiresAt));
                    AddParameter(update, database.DatabaseType, "modifiedat", FormatDate(DateTimeOffset.UtcNow));
                    AddParameter(update, database.DatabaseType, "jobid", normalizedJobId);
                    AddParameter(update, database.DatabaseType, "validationstage", stage);
                    update.ExecuteNonQuery();
                    return;
                }

                using var insert = CreateCommand(database, $"""
                    INSERT INTO {TableName}
                        (jobid, validationstage, tokenhash, expiresat, reportcontenthash, resultcontenthash, createdat, modifiedat)
                    VALUES
                        (@jobid, @validationstage, @tokenhash, @expiresat, @reportcontenthash, @resultcontenthash, @createdat, @modifiedat)
                    """);
                var now = DateTimeOffset.UtcNow;
                AddParameter(insert, database.DatabaseType, "jobid", normalizedJobId);
                AddParameter(insert, database.DatabaseType, "validationstage", stage);
                AddParameter(insert, database.DatabaseType, "tokenhash", tokenHash);
                AddParameter(insert, database.DatabaseType, "expiresat", FormatDate(expiresAt));
                AddParameter(insert, database.DatabaseType, "reportcontenthash", DBNull.Value);
                AddParameter(insert, database.DatabaseType, "resultcontenthash", DBNull.Value);
                AddParameter(insert, database.DatabaseType, "createdat", FormatDate(now));
                AddParameter(insert, database.DatabaseType, "modifiedat", FormatDate(now));
                insert.ExecuteNonQuery();
            });
        }

        return token;
    }

    public PlaywrightUploadAuthorization AuthorizeUpload(
        string jobId,
        PlaywrightValidationMode mode,
        PlaywrightUploadKind kind,
        string token,
        string contentHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(contentHash))
        {
            return PlaywrightUploadAuthorization.Failed("Upload authorization is missing.");
        }

        lock (syncRoot)
        {
            return TryRun(database =>
            {
                EnsureSchema(database);
                PruneExpired(database);

                var normalizedJobId = NormalizeJobId(jobId);
                var stage = ToStage(mode);
                var entry = LoadTokenEntry(database, normalizedJobId, stage);
                if (entry is null)
                {
                    return PlaywrightUploadAuthorization.Failed("Upload authorization is invalid or expired.");
                }

                if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    DeleteTokenEntry(database, normalizedJobId, stage);
                    return PlaywrightUploadAuthorization.Failed("Upload authorization is expired.");
                }

                if (!FixedTimeEquals(entry.TokenHash, ComputeSha256Hash(token.Trim())))
                {
                    return PlaywrightUploadAuthorization.Failed("Upload authorization is invalid.");
                }

                var existingHash = kind == PlaywrightUploadKind.Report
                    ? entry.ReportContentHash
                    : entry.ResultContentHash;
                if (!string.IsNullOrWhiteSpace(existingHash))
                {
                    return FixedTimeEquals(existingHash, contentHash)
                        ? PlaywrightUploadAuthorization.DuplicateAccepted()
                        : PlaywrightUploadAuthorization.Failed("A different upload was already received for this job and validation stage.");
                }

                UpdateContentHash(database, normalizedJobId, stage, kind, contentHash);
                return PlaywrightUploadAuthorization.Accepted();
            }) ?? PlaywrightUploadAuthorization.Failed("Upload authorization could not be verified.");
        }
    }

    public void ResetUpload(string jobId, PlaywrightValidationMode mode, PlaywrightUploadKind kind, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        lock (syncRoot)
        {
            TryRun(database =>
            {
                EnsureSchema(database);
                var normalizedJobId = NormalizeJobId(jobId);
                var stage = ToStage(mode);
                var columnName = kind == PlaywrightUploadKind.Report ? "reportcontenthash" : "resultcontenthash";
                using var command = CreateCommand(database, $"""
                    UPDATE {TableName}
                    SET {columnName} = @contenthash, modifiedat = @modifiedat
                    WHERE jobid = @jobid AND validationstage = @validationstage AND {columnName} = @existinghash
                    """);
                AddParameter(command, database.DatabaseType, "contenthash", DBNull.Value);
                AddParameter(command, database.DatabaseType, "modifiedat", FormatDate(DateTimeOffset.UtcNow));
                AddParameter(command, database.DatabaseType, "jobid", normalizedJobId);
                AddParameter(command, database.DatabaseType, "validationstage", stage);
                AddParameter(command, database.DatabaseType, "existinghash", contentHash);
                command.ExecuteNonQuery();
            });
        }
    }

    public void Revoke(string jobId, PlaywrightValidationMode mode)
    {
        lock (syncRoot)
        {
            TryRun(database =>
            {
                EnsureSchema(database);
                DeleteTokenEntry(database, NormalizeJobId(jobId), ToStage(mode));
            });
        }
    }

    private T? TryRun<T>(Func<UploadTokenConnection, T> action)
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
                    "Playwright upload token persistence operation timed out after {TimeoutSeconds} seconds.",
                    OperationTimeout.TotalSeconds);
                return default;
            }

            return task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Playwright upload token persistence operation failed.");
            return default;
        }
    }

    private void TryRun(Action<UploadTokenConnection> action)
    {
        TryRun(database =>
        {
            action(database);
            return true;
        });
    }

    private UploadTokenConnection OpenConnection()
    {
        using var scope = scopeFactory.CreateScope();
        var discoveryService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseDiscoveryService>();
        var master = discoveryService.DiscoverMasterDatabaseAsync().GetAwaiter().GetResult();
        var connection = CreateConnection(master.DatabaseType, master.ConnectionString);
        connection.Open();
        return new UploadTokenConnection(master.DatabaseType, connection);
    }

    private void EnsureSchema(UploadTokenConnection database)
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
                logger.LogInformation("Upgrade Center Playwright upload token table was created in the master database.");
            }

            schemaReady = true;
        }
        finally
        {
            schemaLock.Release();
        }
    }

    private static bool TableExists(UploadTokenConnection database)
    {
        var sql = database.DatabaseType switch
        {
            DatabaseType.PostgreSQL => "SELECT 1 FROM information_schema.tables WHERE lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.MSSQL => "SELECT OBJECT_ID(@tableName, 'U')",
            DatabaseType.MySQL => "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND lower(table_name) = lower(@tableName) LIMIT 1",
            DatabaseType.Oracle => "SELECT 1 FROM all_tables WHERE table_name = :tableName AND ROWNUM = 1",
            _ => throw new NotSupportedException($"Database type '{database.DatabaseType}' is not supported for Playwright upload token persistence.")
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
                CREATE TABLE bolduc_playwrightuploadtoken (
                    jobid varchar(128) NOT NULL,
                    validationstage varchar(32) NOT NULL,
                    tokenhash varchar(128) NOT NULL,
                    expiresat varchar(40) NOT NULL,
                    reportcontenthash varchar(128) NULL,
                    resultcontenthash varchar(128) NULL,
                    createdat varchar(40) NOT NULL,
                    modifiedat varchar(40) NOT NULL,
                    PRIMARY KEY (jobid, validationstage)
                )
                """,
            DatabaseType.MSSQL => """
                CREATE TABLE bolduc_playwrightuploadtoken (
                    jobid nvarchar(128) NOT NULL,
                    validationstage nvarchar(32) NOT NULL,
                    tokenhash nvarchar(128) NOT NULL,
                    expiresat nvarchar(40) NOT NULL,
                    reportcontenthash nvarchar(128) NULL,
                    resultcontenthash nvarchar(128) NULL,
                    createdat nvarchar(40) NOT NULL,
                    modifiedat nvarchar(40) NOT NULL,
                    CONSTRAINT pk_bolduc_playwrightuploadtoken PRIMARY KEY (jobid, validationstage)
                )
                """,
            DatabaseType.MySQL => """
                CREATE TABLE bolduc_playwrightuploadtoken (
                    jobid varchar(128) NOT NULL,
                    validationstage varchar(32) NOT NULL,
                    tokenhash varchar(128) NOT NULL,
                    expiresat varchar(40) NOT NULL,
                    reportcontenthash varchar(128) NULL,
                    resultcontenthash varchar(128) NULL,
                    createdat varchar(40) NOT NULL,
                    modifiedat varchar(40) NOT NULL,
                    PRIMARY KEY (jobid, validationstage)
                )
                """,
            DatabaseType.Oracle => """
                CREATE TABLE bolduc_playwrightuploadtoken (
                    jobid varchar2(128) NOT NULL,
                    validationstage varchar2(32) NOT NULL,
                    tokenhash varchar2(128) NOT NULL,
                    expiresat varchar2(40) NOT NULL,
                    reportcontenthash varchar2(128) NULL,
                    resultcontenthash varchar2(128) NULL,
                    createdat varchar2(40) NOT NULL,
                    modifiedat varchar2(40) NOT NULL,
                    CONSTRAINT pk_bolduc_playwrightuploadtoken PRIMARY KEY (jobid, validationstage)
                )
                """,
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for Playwright upload token persistence.")
        };
    }

    private static bool TokenEntryExists(UploadTokenConnection database, string jobId, string stage)
    {
        using var command = CreateCommand(database, $"SELECT tokenhash FROM {TableName} WHERE jobid = @jobid AND validationstage = @validationstage");
        AddParameter(command, database.DatabaseType, "jobid", jobId);
        AddParameter(command, database.DatabaseType, "validationstage", stage);
        var result = command.ExecuteScalar();
        return result is not null && result != DBNull.Value;
    }

    private static PersistedUploadToken? LoadTokenEntry(UploadTokenConnection database, string jobId, string stage)
    {
        using var command = CreateCommand(database, $"""
            SELECT tokenhash, expiresat, reportcontenthash, resultcontenthash
            FROM {TableName}
            WHERE jobid = @jobid AND validationstage = @validationstage
            """);
        AddParameter(command, database.DatabaseType, "jobid", jobId);
        AddParameter(command, database.DatabaseType, "validationstage", stage);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        _ = DateTimeOffset.TryParse(reader.GetValue(1)?.ToString(), out var expiresAt);
        return new PersistedUploadToken(
            reader.GetValue(0)?.ToString() ?? string.Empty,
            expiresAt == default ? DateTimeOffset.MinValue : expiresAt,
            reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString(),
            reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString());
    }

    private static void UpdateContentHash(
        UploadTokenConnection database,
        string jobId,
        string stage,
        PlaywrightUploadKind kind,
        string contentHash)
    {
        var columnName = kind == PlaywrightUploadKind.Report ? "reportcontenthash" : "resultcontenthash";
        using var command = CreateCommand(database, $"""
            UPDATE {TableName}
            SET {columnName} = @contenthash, modifiedat = @modifiedat
            WHERE jobid = @jobid AND validationstage = @validationstage
            """);
        AddParameter(command, database.DatabaseType, "contenthash", contentHash);
        AddParameter(command, database.DatabaseType, "modifiedat", FormatDate(DateTimeOffset.UtcNow));
        AddParameter(command, database.DatabaseType, "jobid", jobId);
        AddParameter(command, database.DatabaseType, "validationstage", stage);
        command.ExecuteNonQuery();
    }

    private static void DeleteTokenEntry(UploadTokenConnection database, string jobId, string stage)
    {
        using var command = CreateCommand(database, $"DELETE FROM {TableName} WHERE jobid = @jobid AND validationstage = @validationstage");
        AddParameter(command, database.DatabaseType, "jobid", jobId);
        AddParameter(command, database.DatabaseType, "validationstage", stage);
        command.ExecuteNonQuery();
    }

    private static void PruneExpired(UploadTokenConnection database)
    {
        using var command = CreateCommand(database, $"DELETE FROM {TableName} WHERE expiresat <= @now");
        AddParameter(command, database.DatabaseType, "now", FormatDate(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static DbConnection CreateConnection(DatabaseType databaseType, string connectionString)
    {
        return databaseType switch
        {
            DatabaseType.PostgreSQL => new NpgsqlConnection(connectionString),
            DatabaseType.MSSQL => new SqlConnection(connectionString),
            DatabaseType.MySQL => new MySqlConnection(connectionString),
            DatabaseType.Oracle => new OracleConnection(connectionString),
            _ => throw new NotSupportedException($"Database type '{databaseType}' is not supported for Playwright upload token persistence.")
        };
    }

    private static DbCommand CreateCommand(UploadTokenConnection database, string sql)
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

    private static string NormalizeJobId(string jobId)
    {
        return string.IsNullOrWhiteSpace(jobId) ? string.Empty : jobId.Trim();
    }

    private static string ToStage(PlaywrightValidationMode mode)
    {
        return mode switch
        {
            PlaywrightValidationMode.Post => "post-upgrade",
            PlaywrightValidationMode.Cleanup => "cleanup",
            _ => "pre-upgrade"
        };
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O");
    }

    private static string ComputeSha256Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed record PersistedUploadToken(
        string TokenHash,
        DateTimeOffset ExpiresAt,
        string? ReportContentHash,
        string? ResultContentHash);

    private sealed record UploadTokenConnection(DatabaseType DatabaseType, DbConnection Connection) : IDisposable
    {
        public void Dispose()
        {
            Connection.Dispose();
        }
    }
}
