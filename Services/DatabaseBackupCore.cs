using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public enum DatabaseType
{
    MSSQL,
    MySQL,
    MSSQLCE,
    Oracle,
    PostgreSQL
}

public sealed class BackupOptions
{
    public required string SourceConnectionString { get; init; }
    public required string TargetConnectionString { get; init; }
    public required DatabaseType DatabaseType { get; init; }
    public bool DropAndRecreate { get; init; } = true;
    public int BatchSize { get; init; } = 1000;
    public IReadOnlyList<string> IncludeTables { get; init; } = [];
    public bool DisableFKDuringLoad { get; init; } = true;
}

public sealed class RestoreOptions
{
    public required string BackupConnectionString { get; init; }
    public required string TargetConnectionString { get; init; }
    public required DatabaseType DatabaseType { get; init; }
    public bool DropAndRecreate { get; init; } = true;
    public int BatchSize { get; init; } = 1000;
    public IReadOnlyList<string> IncludeTables { get; init; } = [];
    public bool DisableFKDuringLoad { get; init; } = true;
}

public sealed class TableBackupResult
{
    public required string TableName { get; init; }
    public long RowsCopied { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class BackupStatus
{
    public bool Success { get; init; }
    public string SourceDatabase { get; init; } = string.Empty;
    public string TargetDatabase { get; init; } = string.Empty;
    public DatabaseType DatabaseType { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public TimeSpan TotalElapsed => CompletedAt - StartedAt;
    public int TablesAttempted { get; init; }
    public int TablesSucceeded { get; init; }
    public int TablesFailed { get; init; }
    public long TotalRowsCopied { get; init; }
    public IReadOnlyList<TableBackupResult> TableResults { get; init; } = [];
    public string? FatalError { get; init; }
}

public sealed class TableRestoreResult
{
    public required string TableName { get; init; }
    public long RowsRestored { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class RestoreStatus
{
    public bool Success { get; init; }
    public string BackupDatabase { get; init; } = string.Empty;
    public string TargetDatabase { get; init; } = string.Empty;
    public DatabaseType DatabaseType { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public TimeSpan TotalElapsed => CompletedAt - StartedAt;
    public int TablesAttempted { get; init; }
    public int TablesSucceeded { get; init; }
    public int TablesFailed { get; init; }
    public long TotalRowsRestored { get; init; }
    public IReadOnlyList<TableRestoreResult> TableResults { get; init; } = [];
    public string? FatalError { get; init; }
}

public interface IBackupProgress
{
    void OnPhaseStarted(string phase);
    void OnTableStarted(string tableName, int tableIndex, int totalTables);
    void OnTableProgress(string tableName, long rowsCopied);
    void OnTableCompleted(TableBackupResult result);
    void OnWarning(string message);
    void OnError(string context, Exception ex);
}

public static class ProviderFactory
{
    public static IDatabaseProvider Create(DatabaseType type) => type switch
    {
        DatabaseType.PostgreSQL => new PostgreSqlBackupProvider(),
        DatabaseType.MSSQL => new SqlServerBackupProvider(),
        DatabaseType.MySQL => new MySqlBackupProvider(),
        DatabaseType.Oracle => new OracleBackupProvider(),
        _ => throw new NotSupportedException($"Database type '{type}' is not supported.")
    };
}

public sealed class BackupEngine
{
    private readonly IBackupProgress progress;

    public BackupEngine(IBackupProgress progress)
    {
        this.progress = progress;
    }

    public async Task<BackupStatus> RunAsync(
        IDatabaseProvider source,
        IDatabaseProvider target,
        BackupOptions options,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var results = new List<TableBackupResult>();
        string? fatalError = null;

        try
        {
            progress.OnPhaseStarted("Connecting to source database.");
            await source.OpenAsync(options.SourceConnectionString, cancellationToken);

            progress.OnPhaseStarted("Connecting to backup database.");
            await target.OpenAsync(options.TargetConnectionString, cancellationToken);

            progress.OnPhaseStarted("Discovering source tables.");
            var tables = await ResolveTablesAsync(source, options.IncludeTables, "source", cancellationToken);

            if (options.DisableFKDuringLoad)
            {
                await target.DisableForeignKeysAsync(tables, cancellationToken);
            }

            for (var index = 0; index < tables.Count; index++)
            {
                var table = tables[index];
                progress.OnTableStarted(table, index + 1, tables.Count);
                var started = DateTime.UtcNow;
                long copied = 0;
                string? error = null;
                var success = false;

                try
                {
                    if (options.DropAndRecreate)
                    {
                        await target.DropTableIfExistsAsync(table, cancellationToken);
                    }

                    var columns = await source.GetTableColumnsAsync(table, cancellationToken);
                    await target.CreateTableLikeAsync(table, columns, source, cancellationToken);

                    var rowProgress = new Progress<long>(rows =>
                    {
                        copied = rows;
                        progress.OnTableProgress(table, rows);
                    });

                    await source.CopyTableDataAsync(table, columns, target, options.BatchSize, rowProgress, cancellationToken);
                    success = true;
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    progress.OnError($"Backup table '{table}'", exception);
                }

                var result = new TableBackupResult
                {
                    TableName = table,
                    RowsCopied = copied,
                    Elapsed = DateTime.UtcNow - started,
                    Success = success,
                    ErrorMessage = error
                };
                results.Add(result);
                progress.OnTableCompleted(result);
            }

            await target.CompleteTableRestoreAsync(tables, cancellationToken);

            if (options.DisableFKDuringLoad)
            {
                await target.EnableForeignKeysAsync(tables, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            fatalError = "Backup cancelled.";
        }
        catch (Exception exception)
        {
            fatalError = exception.Message;
            progress.OnError("Backup engine", exception);
        }

        return new BackupStatus
        {
            Success = fatalError is null && results.All(result => result.Success),
            SourceDatabase = source.DatabaseName,
            TargetDatabase = target.DatabaseName,
            DatabaseType = source.DatabaseType,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            TablesAttempted = results.Count,
            TablesSucceeded = results.Count(result => result.Success),
            TablesFailed = results.Count(result => !result.Success),
            TotalRowsCopied = results.Sum(result => result.RowsCopied),
            TableResults = results,
            FatalError = fatalError
        };
    }

    private async Task<IReadOnlyList<string>> ResolveTablesAsync(
        IDatabaseProvider source,
        IReadOnlyList<string> requestedTables,
        string databaseRole,
        CancellationToken cancellationToken)
    {
        if (requestedTables.Count == 0)
        {
            return await source.GetTableNamesAsync(cancellationToken);
        }

        var availableTables = await source.GetTableNamesAsync(cancellationToken);
        var availableTableSet = availableTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableTableLookup = availableTables
            .GroupBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolvedTables = requestedTables
            .Where(availableTableLookup.ContainsKey)
            .Select(table => availableTableLookup[table])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingTables = requestedTables
            .Where(table => !availableTableSet.Contains(table))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var missingTable in missingTables)
        {
            progress.OnWarning($"Affected table '{missingTable}' was not found in the {databaseRole} database and will be skipped.");
        }

        if (resolvedTables.Count == 0)
        {
            progress.OnWarning($"None of the requested affected tables were found in the {databaseRole} database.");
        }

        return resolvedTables;
    }
}

public sealed class RestoreEngine
{
    private readonly IBackupProgress progress;

    public RestoreEngine(IBackupProgress progress)
    {
        this.progress = progress;
    }

    public async Task<RestoreStatus> RunAsync(
        IDatabaseProvider backup,
        IDatabaseProvider target,
        RestoreOptions options,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var results = new List<TableRestoreResult>();
        string? fatalError = null;

        try
        {
            progress.OnPhaseStarted("Connecting to backup database.");
            await backup.OpenAsync(options.BackupConnectionString, cancellationToken);

            progress.OnPhaseStarted("Connecting to target database.");
            await target.OpenAsync(options.TargetConnectionString, cancellationToken);

            progress.OnPhaseStarted("Discovering backup tables.");
            var tables = await ResolveTablesAsync(backup, options.IncludeTables, "backup", cancellationToken);

            if (options.DisableFKDuringLoad)
            {
                await target.DisableForeignKeysAsync(tables, cancellationToken);
            }

            for (var index = 0; index < tables.Count; index++)
            {
                var table = tables[index];
                progress.OnTableStarted(table, index + 1, tables.Count);
                var started = DateTime.UtcNow;
                long restored = 0;
                string? error = null;
                var success = false;

                try
                {
                    if (options.DropAndRecreate)
                    {
                        await target.DropTableIfExistsAsync(table, cancellationToken);
                    }

                    var columns = await backup.GetTableColumnsAsync(table, cancellationToken);
                    await target.CreateTableLikeAsync(table, columns, backup, cancellationToken);

                    var rowProgress = new Progress<long>(rows =>
                    {
                        restored = rows;
                        progress.OnTableProgress(table, rows);
                    });

                    await backup.CopyTableDataAsync(table, columns, target, options.BatchSize, rowProgress, cancellationToken);
                    success = true;
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    progress.OnError($"Restore table '{table}'", exception);
                }

                var result = new TableRestoreResult
                {
                    TableName = table,
                    RowsRestored = restored,
                    Elapsed = DateTime.UtcNow - started,
                    Success = success,
                    ErrorMessage = error
                };
                results.Add(result);
                progress.OnTableCompleted(new TableBackupResult
                {
                    TableName = table,
                    RowsCopied = restored,
                    Elapsed = result.Elapsed,
                    Success = success,
                    ErrorMessage = error
                });
            }

            await target.CompleteTableRestoreAsync(tables, cancellationToken);

            if (options.DisableFKDuringLoad)
            {
                await target.EnableForeignKeysAsync(tables, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            fatalError = "Restore cancelled.";
        }
        catch (Exception exception)
        {
            fatalError = exception.Message;
            progress.OnError("Restore engine", exception);
        }

        return new RestoreStatus
        {
            Success = fatalError is null && results.All(result => result.Success),
            BackupDatabase = backup.DatabaseName,
            TargetDatabase = target.DatabaseName,
            DatabaseType = backup.DatabaseType,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            TablesAttempted = results.Count,
            TablesSucceeded = results.Count(result => result.Success),
            TablesFailed = results.Count(result => !result.Success),
            TotalRowsRestored = results.Sum(result => result.RowsRestored),
            TableResults = results,
            FatalError = fatalError
        };
    }

    private async Task<IReadOnlyList<string>> ResolveTablesAsync(
        IDatabaseProvider source,
        IReadOnlyList<string> requestedTables,
        string databaseRole,
        CancellationToken cancellationToken)
    {
        if (requestedTables.Count == 0)
        {
            return await source.GetTableNamesAsync(cancellationToken);
        }

        var availableTables = await source.GetTableNamesAsync(cancellationToken);
        var availableTableSet = availableTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableTableLookup = availableTables
            .GroupBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolvedTables = requestedTables
            .Where(availableTableLookup.ContainsKey)
            .Select(table => availableTableLookup[table])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingTables = requestedTables
            .Where(table => !availableTableSet.Contains(table))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var missingTable in missingTables)
        {
            progress.OnWarning($"Requested restore table '{missingTable}' was not found in the {databaseRole} database and will be skipped.");
        }

        if (resolvedTables.Count == 0)
        {
            progress.OnWarning($"None of the requested restore tables were found in the {databaseRole} database.");
        }

        return resolvedTables;
    }
}

public sealed class BackupColumn
{
    public required string Name { get; init; }
    public required string StoreType { get; init; }
    public bool IsNullable { get; init; }
    public int Ordinal { get; init; }
}

public interface IDatabaseProvider : IAsyncDisposable
{
    string DatabaseName { get; }
    DatabaseType DatabaseType { get; }
    Task OpenAsync(string connectionString, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetTableNamesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupColumn>> GetTableColumnsAsync(string tableName, CancellationToken cancellationToken = default);
    Task<string?> GetCreateTableScriptAsync(string tableName, CancellationToken cancellationToken = default);
    Task CreateTableAsync(string tableName, IReadOnlyList<BackupColumn> columns, CancellationToken cancellationToken = default);
    Task CreateTableLikeAsync(string tableName, IReadOnlyList<BackupColumn> columns, IDatabaseProvider sourceProvider, CancellationToken cancellationToken = default);
    Task DropTableIfExistsAsync(string tableName, CancellationToken cancellationToken = default);
    Task DisableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default);
    Task CompleteTableRestoreAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default);
    Task EnableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default);
    Task CopyTableDataAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IDatabaseProvider target,
        int batchSize,
        IProgress<long> rowProgress,
        CancellationToken cancellationToken = default);
    Task BulkInsertAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken = default);
}

public abstract class DbBackupProviderBase : IDatabaseProvider
{
    protected DbConnection? Connection { get; private set; }

    public string DatabaseName { get; protected set; } = string.Empty;
    public abstract DatabaseType DatabaseType { get; }

    public async Task OpenAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        Connection = CreateConnection(connectionString);
        await Connection.OpenAsync(cancellationToken);
        DatabaseName = Connection.Database;
    }

    public abstract Task<IReadOnlyList<string>> GetTableNamesAsync(CancellationToken cancellationToken = default);
    protected abstract DbConnection CreateConnection(string connectionString);
    protected abstract string QuoteIdentifier(string identifier);
    protected abstract string GetParameterName(int index);
    protected abstract string BuildTableReference(string tableName);
    protected abstract string MapStoreType(string storeType);

    protected static (string? SchemaName, string TableName) SplitTableIdentifier(string tableName, string? defaultSchema = null)
    {
        var parts = tableName
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('"', '`', '[', ']'))
            .ToArray();

        return parts.Length >= 2
            ? (parts[^2], parts[^1])
            : (defaultSchema, parts.Length == 0 ? tableName : parts[0]);
    }

    protected static string FormatDiscoveredTableName(string schemaName, string tableName, string defaultSchema)
    {
        return schemaName.Equals(defaultSchema, StringComparison.OrdinalIgnoreCase)
            ? tableName
            : $"{schemaName}.{tableName}";
    }

    public virtual async Task<IReadOnlyList<BackupColumn>> GetTableColumnsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandText = $"SELECT * FROM {BuildTableReference(tableName)} WHERE 1 = 0";

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);
        var schema = await reader.GetColumnSchemaAsync(cancellationToken);

        return schema
            .OrderBy(column => column.ColumnOrdinal ?? 0)
            .Select((column, index) => new BackupColumn
            {
                Name = column.ColumnName ?? $"Column{index}",
                StoreType = column.DataTypeName ?? MapClrType(column.DataType),
                IsNullable = column.AllowDBNull ?? true,
                Ordinal = index
            })
            .ToList();
    }

    public virtual Task<string?> GetCreateTableScriptAsync(string tableName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public virtual async Task CreateTableAsync(string tableName, IReadOnlyList<BackupColumn> columns, CancellationToken cancellationToken = default)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"Cannot create backup table '{tableName}' because no columns were discovered.");
        }

        var columnDefinitions = columns.Select(column =>
        {
            var nullability = column.IsNullable ? "NULL" : "NOT NULL";
            return $"{QuoteIdentifier(column.Name)} {MapStoreType(column.StoreType)} {nullability}";
        });

        await ExecuteAsync(
            $"CREATE TABLE {BuildTableReference(tableName)} ({string.Join(", ", columnDefinitions)})",
            cancellationToken);
    }

    public virtual Task CreateTableLikeAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IDatabaseProvider sourceProvider,
        CancellationToken cancellationToken = default)
    {
        return CreateTableAsync(tableName, columns, cancellationToken);
    }

    public abstract Task DropTableIfExistsAsync(string tableName, CancellationToken cancellationToken = default);

    public virtual Task DisableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public virtual Task EnableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public virtual Task CompleteTableRestoreAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public virtual async Task CopyTableDataAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IDatabaseProvider target,
        int batchSize,
        IProgress<long> rowProgress,
        CancellationToken cancellationToken = default)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)))} FROM {BuildTableReference(tableName)}";

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var batch = new List<object?[]>(Math.Max(1, batchSize));
        long total = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[columns.Count];
            for (var index = 0; index < columns.Count; index++)
            {
                row[index] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? null
                    : reader.GetValue(index);
            }

            batch.Add(row);
            total++;

            if (batch.Count >= batchSize)
            {
                await target.BulkInsertAsync(tableName, columns, batch, cancellationToken);
                batch.Clear();
                rowProgress.Report(total);
            }
        }

        if (batch.Count > 0)
        {
            await target.BulkInsertAsync(tableName, columns, batch, cancellationToken);
            rowProgress.Report(total);
        }
    }

    public virtual async Task BulkInsertAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columnList = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
        var parameterList = string.Join(", ", Enumerable.Range(0, columns.Count).Select(GetParameterName));
        var insertSql = $"INSERT INTO {BuildTableReference(tableName)} ({columnList}) VALUES ({parameterList})";

        await using var transaction = await Connection!.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var row in rows)
            {
                await using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insertSql;

                for (var index = 0; index < columns.Count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = GetParameterName(index);
                    parameter.Value = row[index] ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
            Connection = null;
        }

        GC.SuppressFinalize(this);
    }

    protected async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string MapClrType(Type? clrType)
    {
        if (clrType == typeof(int))
        {
            return "integer";
        }

        if (clrType == typeof(long))
        {
            return "bigint";
        }

        if (clrType == typeof(bool))
        {
            return "boolean";
        }

        if (clrType == typeof(DateTime) || clrType == typeof(DateTimeOffset))
        {
            return "timestamp";
        }

        if (clrType == typeof(decimal))
        {
            return "numeric";
        }

        if (clrType == typeof(byte[]))
        {
            return "bytea";
        }

        return "text";
    }
}

public sealed class PostgreSqlBackupProvider : DbBackupProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.PostgreSQL;

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public override async Task<IReadOnlyList<string>> GetTableNamesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              AND table_type = 'BASE TABLE'
            ORDER BY table_schema, table_name
            """;

        return await ReadTableNamesAsync(sql, cancellationToken);
    }

    public override Task DropTableIfExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync($"DROP TABLE IF EXISTS {BuildTableReference(tableName)} CASCADE", cancellationToken);
    }

    protected override string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    protected override string GetParameterName(int index) => $"@p{index}";
    protected override string BuildTableReference(string tableName)
    {
        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "public");
        return $"{QuoteIdentifier(schemaName ?? "public")}.{QuoteIdentifier(actualTableName)}";
    }

    protected override string MapStoreType(string storeType)
    {
        var normalized = storeType.ToLowerInvariant();
        return normalized switch
        {
            "int" or "int4" or "integer" => "INTEGER",
            "int8" or "bigint" => "BIGINT",
            "int2" or "smallint" => "SMALLINT",
            "bool" or "boolean" => "BOOLEAN",
            "float4" or "real" => "REAL",
            "float8" or "double precision" => "DOUBLE PRECISION",
            "decimal" or "numeric" => "NUMERIC",
            "varchar" or "character varying" => "TEXT",
            "char" or "character" => "TEXT",
            "timestamp" or "timestamp without time zone" => "TIMESTAMP",
            "timestamptz" or "timestamp with time zone" => "TIMESTAMPTZ",
            "bytea" => "BYTEA",
            "uuid" => "UUID",
            "json" => "JSON",
            "jsonb" => "JSONB",
            "date" => "DATE",
            "time" or "time without time zone" => "TIME",
            _ => normalized.Contains("char", StringComparison.Ordinal) ? "TEXT" : storeType.ToUpperInvariant()
        };
    }

    private async Task<IReadOnlyList<string>> ReadTableNamesAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(FormatDiscoveredTableName(reader.GetString(0), reader.GetString(1), "public"));
        }

        return tables;
    }
}

public sealed class SqlServerBackupProvider : DbBackupProviderBase
{
    private readonly Dictionary<string, string> pendingForeignKeyScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> pendingSecurityPolicyScripts = new(StringComparer.OrdinalIgnoreCase);

    public override DatabaseType DatabaseType => DatabaseType.MSSQL;

    protected override DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public override async Task<IReadOnlyList<string>> GetTableNamesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(FormatDiscoveredTableName(reader.GetString(0), reader.GetString(1), "dbo"));
        }

        return tables;
    }

    public override async Task<string?> GetCreateTableScriptAsync(string tableName, CancellationToken cancellationToken = default)
    {
        var columns = await ReadColumnDefinitionsAsync(tableName, cancellationToken);
        if (columns.Count == 0)
        {
            return null;
        }

        var keyConstraints = await ReadKeyConstraintsAsync(tableName, cancellationToken);
        var definitions = columns
            .Select(BuildColumnDefinition)
            .Concat(BuildKeyConstraintDefinitions(keyConstraints))
            .ToList();

        var builder = new StringBuilder();
        builder.Append("CREATE TABLE ");
        builder.Append(BuildTableReference(tableName));
        builder.AppendLine(" (");
        builder.Append("    ");
        builder.Append(string.Join("," + Environment.NewLine + "    ", definitions));
        builder.AppendLine();
        builder.Append(')');

        return builder.ToString();
    }

    public override async Task CreateTableLikeAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IDatabaseProvider sourceProvider,
        CancellationToken cancellationToken = default)
    {
        var createTableScript = await sourceProvider.GetCreateTableScriptAsync(tableName, cancellationToken);
        if (string.IsNullOrWhiteSpace(createTableScript))
        {
            await base.CreateTableLikeAsync(tableName, columns, sourceProvider, cancellationToken);
            return;
        }

        await ExecuteAsync(RewriteCreateTableName(createTableScript, tableName), cancellationToken);
    }

    public override async Task BulkInsertAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var hasIdentityColumn = await HasIdentityColumnAsync(tableName, columns, cancellationToken);
        if (!hasIdentityColumn)
        {
            await base.BulkInsertAsync(tableName, columns, rows, cancellationToken);
            return;
        }

        var columnList = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
        var parameterList = string.Join(", ", Enumerable.Range(0, columns.Count).Select(GetParameterName));
        var insertSql = $"INSERT INTO {BuildTableReference(tableName)} ({columnList}) VALUES ({parameterList})";

        await using var transaction = await Connection!.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteInTransactionAsync($"SET IDENTITY_INSERT {BuildTableReference(tableName)} ON", transaction, cancellationToken);

            foreach (var row in rows)
            {
                await using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insertSql;

                for (var index = 0; index < columns.Count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = GetParameterName(index);
                    parameter.Value = row[index] ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await ExecuteInTransactionAsync($"SET IDENTITY_INSERT {BuildTableReference(tableName)} OFF", transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await ExecuteInTransactionAsync($"SET IDENTITY_INSERT {BuildTableReference(tableName)} OFF", transaction, cancellationToken);
            }
            catch
            {
                // Best effort cleanup before rollback. The original restore error is more useful to the caller.
            }

            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public override async Task DropTableIfExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        var escapedObjectName = $"{(schemaName ?? "dbo").Replace("'", "''", StringComparison.Ordinal)}.{actualTableName.Replace("'", "''", StringComparison.Ordinal)}";
        await CaptureAndDropSecurityPoliciesAsync(tableName, cancellationToken);
        await CaptureAndDropForeignKeysAsync(tableName, cancellationToken);
        await ExecuteAsync($"IF OBJECT_ID(N'{escapedObjectName}', N'U') IS NOT NULL DROP TABLE {BuildTableReference(tableName)}", cancellationToken);
    }

    public override async Task DisableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        foreach (var table in tableNames)
        {
            var (schemaName, actualTableName) = SplitTableIdentifier(table, "dbo");
            var escapedObjectName = $"{(schemaName ?? "dbo").Replace("'", "''", StringComparison.Ordinal)}.{actualTableName.Replace("'", "''", StringComparison.Ordinal)}";
            await ExecuteAsync($"IF OBJECT_ID(N'{escapedObjectName}', N'U') IS NOT NULL ALTER TABLE {BuildTableReference(table)} NOCHECK CONSTRAINT ALL", cancellationToken);
        }
    }

    public override async Task EnableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        foreach (var table in tableNames)
        {
            var (schemaName, actualTableName) = SplitTableIdentifier(table, "dbo");
            var escapedObjectName = $"{(schemaName ?? "dbo").Replace("'", "''", StringComparison.Ordinal)}.{actualTableName.Replace("'", "''", StringComparison.Ordinal)}";
            await ExecuteAsync($"IF OBJECT_ID(N'{escapedObjectName}', N'U') IS NOT NULL ALTER TABLE {BuildTableReference(table)} WITH CHECK CHECK CONSTRAINT ALL", cancellationToken);
        }
    }

    public override async Task CompleteTableRestoreAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var script in pendingForeignKeyScripts.Values)
            {
                await ExecuteAsync(script, cancellationToken);
            }

            foreach (var (policyName, script) in pendingSecurityPolicyScripts)
            {
                try
                {
                    await ExecuteAsync(script, cancellationToken);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"SQL Server security policy '{policyName}' could not be recreated after table restore: {exception.Message}",
                        exception);
                }
            }
        }
        finally
        {
            pendingForeignKeyScripts.Clear();
            pendingSecurityPolicyScripts.Clear();
        }
    }

    protected override string QuoteIdentifier(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    protected override string GetParameterName(int index) => $"@p{index}";
    protected override string BuildTableReference(string tableName)
    {
        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        return $"{QuoteIdentifier(schemaName ?? "dbo")}.{QuoteIdentifier(actualTableName)}";
    }

    protected override string MapStoreType(string storeType)
    {
        var normalized = storeType.ToLowerInvariant();
        return normalized switch
        {
            "int" => "INT",
            "bigint" => "BIGINT",
            "smallint" => "SMALLINT",
            "tinyint" => "TINYINT",
            "bit" => "BIT",
            "float" => "FLOAT",
            "real" => "REAL",
            "decimal" or "numeric" or "money" or "smallmoney" => "DECIMAL(38,10)",
            "date" => "DATE",
            "time" => "TIME",
            "datetime" or "datetime2" or "smalldatetime" => "DATETIME2",
            "uniqueidentifier" => "UNIQUEIDENTIFIER",
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "VARBINARY(MAX)",
            "xml" => "XML",
            _ => "NVARCHAR(MAX)"
        };
    }

    private async Task<IReadOnlyList<SqlServerColumnDefinition>> ReadColumnDefinitionsAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        const string sql = """
            DECLARE @qualifiedTable nvarchar(517) = QUOTENAME(@schemaName) + N'.' + QUOTENAME(@tableName);

            SELECT
                c.name AS ColumnName,
                ty.name AS TypeName,
                c.max_length AS MaxLength,
                c.precision AS Precision,
                c.scale AS Scale,
                c.is_nullable AS IsNullable,
                COLUMNPROPERTY(c.object_id, c.name, 'IsIdentity') AS IsIdentity,
                CONVERT(decimal(38, 0), IDENT_SEED(@qualifiedTable)) AS IdentitySeed,
                CONVERT(decimal(38, 0), IDENT_INCR(@qualifiedTable)) AS IdentityIncrement,
                dc.name AS DefaultName,
                dc.definition AS DefaultDefinition,
                c.column_id AS ColumnId
            FROM sys.tables tableInfo
            INNER JOIN sys.schemas schemaInfo ON tableInfo.schema_id = schemaInfo.schema_id
            INNER JOIN sys.columns c ON tableInfo.object_id = c.object_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
            WHERE schemaInfo.name = @schemaName
              AND tableInfo.name = @tableName
            ORDER BY c.column_id;
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@schemaName", schemaName ?? "dbo");
        AddParameter(command, "@tableName", actualTableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<SqlServerColumnDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new SqlServerColumnDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetByte(3),
                reader.GetByte(4),
                reader.GetBoolean(5),
                Convert.ToInt32(reader.GetValue(6)) == 1,
                await reader.IsDBNullAsync(7, cancellationToken) ? null : Convert.ToDecimal(reader.GetValue(7)),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : Convert.ToDecimal(reader.GetValue(8)),
                await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
                await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
                reader.GetInt32(11)));
        }

        return columns;
    }

    private async Task<IReadOnlyList<SqlServerKeyConstraintColumn>> ReadKeyConstraintsAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        const string sql = """
            SELECT
                keyConstraint.name AS ConstraintName,
                keyConstraint.type AS ConstraintType,
                indexInfo.type_desc AS IndexType,
                indexColumn.key_ordinal AS KeyOrdinal,
                columnInfo.name AS ColumnName,
                indexColumn.is_descending_key AS IsDescending
            FROM sys.key_constraints keyConstraint
            INNER JOIN sys.tables tableInfo ON keyConstraint.parent_object_id = tableInfo.object_id
            INNER JOIN sys.schemas schemaInfo ON tableInfo.schema_id = schemaInfo.schema_id
            INNER JOIN sys.indexes indexInfo
                ON keyConstraint.parent_object_id = indexInfo.object_id
               AND keyConstraint.unique_index_id = indexInfo.index_id
            INNER JOIN sys.index_columns indexColumn
                ON indexInfo.object_id = indexColumn.object_id
               AND indexInfo.index_id = indexColumn.index_id
               AND indexColumn.key_ordinal > 0
            INNER JOIN sys.columns columnInfo
                ON indexColumn.object_id = columnInfo.object_id
               AND indexColumn.column_id = columnInfo.column_id
            WHERE schemaInfo.name = @schemaName
              AND tableInfo.name = @tableName
              AND keyConstraint.type IN ('PK', 'UQ')
            ORDER BY keyConstraint.name, indexColumn.key_ordinal;
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@schemaName", schemaName ?? "dbo");
        AddParameter(command, "@tableName", actualTableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<SqlServerKeyConstraintColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new SqlServerKeyConstraintColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3)),
                reader.GetString(4),
                reader.GetBoolean(5)));
        }

        return columns;
    }

    private async Task CaptureAndDropForeignKeysAsync(string tableName, CancellationToken cancellationToken)
    {
        var foreignKeys = await ReadForeignKeysAsync(tableName, cancellationToken);
        foreach (var group in foreignKeys.GroupBy(column => column.ForeignKeyId))
        {
            var first = group.First();
            var key = $"{first.ParentSchema}.{first.ParentTable}.{first.ForeignKeyName}";
            if (!pendingForeignKeyScripts.ContainsKey(key))
            {
                pendingForeignKeyScripts[key] = BuildForeignKeyScript(group.OrderBy(column => column.Ordinal).ToList());
            }

            await ExecuteAsync(
                $"ALTER TABLE {BuildTableReference(first.ParentSchema, first.ParentTable)} DROP CONSTRAINT {QuoteIdentifier(first.ForeignKeyName)}",
                cancellationToken);
        }
    }

    private async Task CaptureAndDropSecurityPoliciesAsync(string tableName, CancellationToken cancellationToken)
    {
        var securityPredicates = await ReadSecurityPredicatesAsync(tableName, cancellationToken);
        foreach (var group in securityPredicates.GroupBy(predicate => predicate.PolicyId))
        {
            var predicates = group.OrderBy(predicate => predicate.PredicateId).ToList();
            var first = predicates[0];
            var key = $"{first.PolicySchema}.{first.PolicyName}";
            if (!pendingSecurityPolicyScripts.ContainsKey(key))
            {
                pendingSecurityPolicyScripts[key] = BuildSecurityPolicyScript(predicates);
                await ExecuteAsync($"DROP SECURITY POLICY {BuildTableReference(first.PolicySchema, first.PolicyName)}", cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<SqlServerSecurityPredicate>> ReadSecurityPredicatesAsync(string tableName, CancellationToken cancellationToken)
    {
        if (!await SupportsSecurityPredicatesAsync(cancellationToken))
        {
            return Array.Empty<SqlServerSecurityPredicate>();
        }

        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        const string sql = """
            WITH ReferencingPolicies AS
            (
                SELECT DISTINCT securityPolicy.object_id AS PolicyId
                FROM sys.security_policies securityPolicy
                INNER JOIN sys.security_predicates securityPredicate ON securityPolicy.object_id = securityPredicate.object_id
                INNER JOIN sys.tables targetTable ON securityPredicate.target_object_id = targetTable.object_id
                INNER JOIN sys.schemas targetSchema ON targetTable.schema_id = targetSchema.schema_id
                WHERE targetSchema.name = @schemaName
                  AND targetTable.name = @tableName
            )
            SELECT
                securityPolicy.object_id AS PolicyId,
                policySchema.name AS PolicySchema,
                securityPolicy.name AS PolicyName,
                securityPolicy.is_enabled AS IsEnabled,
                securityPolicy.is_schema_bound AS IsSchemaBound,
                securityPredicate.security_predicate_id AS PredicateId,
                securityPredicate.predicate_type_desc AS PredicateType,
                securityPredicate.operation_desc AS Operation,
                securityPredicate.predicate_definition AS PredicateDefinition,
                targetSchema.name AS TargetSchema,
                targetTable.name AS TargetTable
            FROM ReferencingPolicies referencingPolicy
            INNER JOIN sys.security_policies securityPolicy ON referencingPolicy.PolicyId = securityPolicy.object_id
            INNER JOIN sys.schemas policySchema ON securityPolicy.schema_id = policySchema.schema_id
            INNER JOIN sys.security_predicates securityPredicate ON securityPolicy.object_id = securityPredicate.object_id
            INNER JOIN sys.tables targetTable ON securityPredicate.target_object_id = targetTable.object_id
            INNER JOIN sys.schemas targetSchema ON targetTable.schema_id = targetSchema.schema_id
            ORDER BY securityPolicy.object_id, securityPredicate.security_predicate_id;
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@schemaName", schemaName ?? "dbo");
        AddParameter(command, "@tableName", actualTableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var predicates = new List<SqlServerSecurityPredicate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            predicates.Add(new SqlServerSecurityPredicate(
                Convert.ToInt32(reader.GetValue(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                Convert.ToInt32(reader.GetValue(5)),
                reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return predicates;
    }

    private async Task<bool> SupportsSecurityPredicatesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE
                WHEN OBJECT_ID(N'sys.security_policies') IS NULL THEN 0
                WHEN OBJECT_ID(N'sys.security_predicates') IS NULL THEN 0
                WHEN COL_LENGTH(N'sys.security_predicates', N'predicate_type_desc') IS NULL THEN 0
                WHEN COL_LENGTH(N'sys.security_predicates', N'predicate_definition') IS NULL THEN 0
                ELSE 1
            END
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) == 1;
    }

    private async Task<IReadOnlyList<SqlServerForeignKeyColumn>> ReadForeignKeysAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        const string sql = """
            SELECT
                foreignKey.object_id AS ForeignKeyId,
                foreignKey.name AS ForeignKeyName,
                parentSchema.name AS ParentSchema,
                parentTable.name AS ParentTable,
                referencedSchema.name AS ReferencedSchema,
                referencedTable.name AS ReferencedTable,
                foreignKey.delete_referential_action_desc AS DeleteAction,
                foreignKey.update_referential_action_desc AS UpdateAction,
                foreignKey.is_not_for_replication AS IsNotForReplication,
                foreignKeyColumn.constraint_column_id AS Ordinal,
                parentColumn.name AS ParentColumn,
                referencedColumn.name AS ReferencedColumn
            FROM sys.foreign_keys foreignKey
            INNER JOIN sys.foreign_key_columns foreignKeyColumn ON foreignKey.object_id = foreignKeyColumn.constraint_object_id
            INNER JOIN sys.tables parentTable ON foreignKey.parent_object_id = parentTable.object_id
            INNER JOIN sys.schemas parentSchema ON parentTable.schema_id = parentSchema.schema_id
            INNER JOIN sys.columns parentColumn
                ON foreignKeyColumn.parent_object_id = parentColumn.object_id
               AND foreignKeyColumn.parent_column_id = parentColumn.column_id
            INNER JOIN sys.tables referencedTable ON foreignKey.referenced_object_id = referencedTable.object_id
            INNER JOIN sys.schemas referencedSchema ON referencedTable.schema_id = referencedSchema.schema_id
            INNER JOIN sys.columns referencedColumn
                ON foreignKeyColumn.referenced_object_id = referencedColumn.object_id
               AND foreignKeyColumn.referenced_column_id = referencedColumn.column_id
            WHERE (parentSchema.name = @schemaName AND parentTable.name = @tableName)
               OR (referencedSchema.name = @schemaName AND referencedTable.name = @tableName)
            ORDER BY foreignKey.object_id, foreignKeyColumn.constraint_column_id;
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@schemaName", schemaName ?? "dbo");
        AddParameter(command, "@tableName", actualTableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var foreignKeys = new List<SqlServerForeignKeyColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            foreignKeys.Add(new SqlServerForeignKeyColumn(
                Convert.ToInt32(reader.GetValue(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetBoolean(8),
                Convert.ToInt32(reader.GetValue(9)),
                reader.GetString(10),
                reader.GetString(11)));
        }

        return foreignKeys;
    }

    private async Task<bool> HasIdentityColumnAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
        {
            return false;
        }

        var (schemaName, actualTableName) = SplitTableIdentifier(tableName, "dbo");
        const string sql = """
            SELECT columnInfo.name
            FROM sys.columns columnInfo
            INNER JOIN sys.tables tableInfo ON columnInfo.object_id = tableInfo.object_id
            INNER JOIN sys.schemas schemaInfo ON tableInfo.schema_id = schemaInfo.schema_id
            WHERE schemaInfo.name = @schemaName
              AND tableInfo.name = @tableName
              AND columnInfo.is_identity = 1;
            """;

        await using var command = Connection!.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@schemaName", schemaName ?? "dbo");
        AddParameter(command, "@tableName", actualTableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var identityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            identityColumns.Add(reader.GetString(0));
        }

        return columns.Any(column => identityColumns.Contains(column.Name));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteInTransactionAsync(
        string sql,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BuildColumnDefinition(SqlServerColumnDefinition column)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIdentifier(column.Name));
        builder.Append(' ');
        builder.Append(FormatStoreType(column));

        if (column.IsIdentity)
        {
            var seed = column.IdentitySeed?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "1";
            var increment = column.IdentityIncrement?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "1";
            builder.Append($" IDENTITY({seed},{increment})");
        }

        builder.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (!string.IsNullOrWhiteSpace(column.DefaultDefinition))
        {
            builder.Append(' ');
            if (!string.IsNullOrWhiteSpace(column.DefaultName))
            {
                builder.Append("CONSTRAINT ");
                builder.Append(QuoteIdentifier(column.DefaultName));
                builder.Append(' ');
            }

            builder.Append("DEFAULT ");
            builder.Append(column.DefaultDefinition);
        }

        return builder.ToString();
    }

    private IEnumerable<string> BuildKeyConstraintDefinitions(IReadOnlyList<SqlServerKeyConstraintColumn> keyColumns)
    {
        foreach (var group in keyColumns.GroupBy(column => column.ConstraintName))
        {
            var first = group.First();
            var keyword = string.Equals(first.ConstraintType, "PK", StringComparison.OrdinalIgnoreCase)
                ? "PRIMARY KEY"
                : "UNIQUE";
            var indexType = first.IndexType.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
                ? first.IndexType.Replace("_", " ", StringComparison.OrdinalIgnoreCase)
                : "NONCLUSTERED";
            var columnList = string.Join(
                ", ",
                group.OrderBy(column => column.KeyOrdinal)
                    .Select(column => $"{QuoteIdentifier(column.ColumnName)} {(column.IsDescending ? "DESC" : "ASC")}"));

            yield return $"CONSTRAINT {QuoteIdentifier(first.ConstraintName)} {keyword} {indexType} ({columnList})";
        }
    }

    private string BuildForeignKeyScript(IReadOnlyList<SqlServerForeignKeyColumn> columns)
    {
        var first = columns[0];
        var parentColumns = string.Join(", ", columns.Select(column => QuoteIdentifier(column.ParentColumn)));
        var referencedColumns = string.Join(", ", columns.Select(column => QuoteIdentifier(column.ReferencedColumn)));
        var builder = new StringBuilder();

        builder.Append("ALTER TABLE ");
        builder.Append(BuildTableReference(first.ParentSchema, first.ParentTable));
        builder.Append(" WITH CHECK ADD CONSTRAINT ");
        builder.Append(QuoteIdentifier(first.ForeignKeyName));
        builder.Append(" FOREIGN KEY (");
        builder.Append(parentColumns);
        builder.Append(") REFERENCES ");
        builder.Append(BuildTableReference(first.ReferencedSchema, first.ReferencedTable));
        builder.Append(" (");
        builder.Append(referencedColumns);
        builder.Append(')');

        AppendReferentialAction(builder, "DELETE", first.DeleteAction);
        AppendReferentialAction(builder, "UPDATE", first.UpdateAction);

        if (first.IsNotForReplication)
        {
            builder.Append(" NOT FOR REPLICATION");
        }

        builder.Append("; ALTER TABLE ");
        builder.Append(BuildTableReference(first.ParentSchema, first.ParentTable));
        builder.Append(" CHECK CONSTRAINT ");
        builder.Append(QuoteIdentifier(first.ForeignKeyName));

        return builder.ToString();
    }

    private string BuildSecurityPolicyScript(IReadOnlyList<SqlServerSecurityPredicate> predicates)
    {
        var first = predicates[0];
        var builder = new StringBuilder();
        builder.Append("CREATE SECURITY POLICY ");
        builder.Append(BuildTableReference(first.PolicySchema, first.PolicyName));
        builder.AppendLine();

        for (var index = 0; index < predicates.Count; index++)
        {
            var predicate = predicates[index];
            if (index > 0)
            {
                builder.AppendLine(",");
            }

            builder.Append("ADD ");
            builder.Append(FormatSecurityPredicateType(predicate.PredicateType));
            builder.Append(" PREDICATE ");
            builder.Append(NormalizeSecurityPredicateDefinition(predicate.PredicateDefinition));
            builder.Append(" ON ");
            builder.Append(BuildTableReference(predicate.TargetSchema, predicate.TargetTable));

            var operation = FormatSecurityPredicateOperation(predicate.Operation);
            if (!string.IsNullOrWhiteSpace(operation))
            {
                builder.Append(' ');
                builder.Append(operation);
            }
        }

        builder.AppendLine();
        builder.Append("WITH (STATE = ");
        builder.Append(first.IsEnabled ? "ON" : "OFF");
        builder.Append(", SCHEMABINDING = ");
        builder.Append(first.IsSchemaBound ? "ON" : "OFF");
        builder.Append(");");

        return builder.ToString();
    }

    private static void AppendReferentialAction(StringBuilder builder, string actionName, string action)
    {
        if (string.Equals(action, "NO_ACTION", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.Append(" ON ");
        builder.Append(actionName);
        builder.Append(' ');
        builder.Append(action.Replace("_", " ", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatSecurityPredicateType(string predicateType)
    {
        return string.Equals(predicateType, "BLOCK", StringComparison.OrdinalIgnoreCase)
            ? "BLOCK"
            : "FILTER";
    }

    private static string FormatSecurityPredicateOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation) ||
            string.Equals(operation, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return operation.Replace("_", " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSecurityPredicateDefinition(string predicateDefinition)
    {
        var definition = predicateDefinition.Trim();
        while (definition.Length > 1 &&
               definition[0] == '(' &&
               definition[^1] == ')' &&
               HasSingleOuterParentheses(definition))
        {
            definition = definition[1..^1].Trim();
        }

        return definition;
    }

    private static bool HasSingleOuterParentheses(string value)
    {
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '(')
            {
                depth++;
            }
            else if (value[index] == ')')
            {
                depth--;
                if (depth == 0 && index < value.Length - 1)
                {
                    return false;
                }
            }

            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private string BuildTableReference(string schemaName, string tableName) => $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}";

    private string RewriteCreateTableName(string createTableScript, string tableName)
    {
        return Regex.Replace(
            createTableScript,
            @"^\s*CREATE\s+TABLE\s+(?:\[[^\]]+\]\.)?\[[^\]]+\]",
            $"CREATE TABLE {BuildTableReference(tableName)}",
            RegexOptions.IgnoreCase);
    }

    private static string FormatStoreType(SqlServerColumnDefinition column)
    {
        var typeName = column.TypeName.ToLowerInvariant();
        return typeName switch
        {
            "nvarchar" or "nchar" => $"{typeName.ToUpperInvariant()}({FormatLength(column.MaxLength, divideByTwo: true)})",
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName.ToUpperInvariant()}({FormatLength(column.MaxLength, divideByTwo: false)})",
            "decimal" or "numeric" => $"{typeName.ToUpperInvariant()}({column.Precision},{column.Scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{typeName.ToUpperInvariant()}({column.Scale})",
            _ => typeName.ToUpperInvariant()
        };
    }

    private static string FormatLength(short maxLength, bool divideByTwo)
    {
        if (maxLength == -1)
        {
            return "MAX";
        }

        return (divideByTwo ? maxLength / 2 : maxLength).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record SqlServerColumnDefinition(
        string Name,
        string TypeName,
        short MaxLength,
        byte Precision,
        byte Scale,
        bool IsNullable,
        bool IsIdentity,
        decimal? IdentitySeed,
        decimal? IdentityIncrement,
        string? DefaultName,
        string? DefaultDefinition,
        int ColumnId);

    private sealed record SqlServerKeyConstraintColumn(
        string ConstraintName,
        string ConstraintType,
        string IndexType,
        int KeyOrdinal,
        string ColumnName,
        bool IsDescending);

    private sealed record SqlServerForeignKeyColumn(
        int ForeignKeyId,
        string ForeignKeyName,
        string ParentSchema,
        string ParentTable,
        string ReferencedSchema,
        string ReferencedTable,
        string DeleteAction,
        string UpdateAction,
        bool IsNotForReplication,
        int Ordinal,
        string ParentColumn,
        string ReferencedColumn);

    private sealed record SqlServerSecurityPredicate(
        int PolicyId,
        string PolicySchema,
        string PolicyName,
        bool IsEnabled,
        bool IsSchemaBound,
        int PredicateId,
        string PredicateType,
        string? Operation,
        string PredicateDefinition,
        string TargetSchema,
        string TargetTable);
}

public sealed class MySqlBackupProvider : DbBackupProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.MySQL;

    protected override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public override async Task<IReadOnlyList<string>> GetTableNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandText = """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public override async Task<string?> GetCreateTableScriptAsync(string tableName, CancellationToken cancellationToken = default)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandText = $"SHOW CREATE TABLE {BuildTableReference(tableName)}";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && reader.FieldCount > 1 && !await reader.IsDBNullAsync(1, cancellationToken))
        {
            return reader.GetString(1);
        }

        return null;
    }

    public override async Task CreateTableLikeAsync(
        string tableName,
        IReadOnlyList<BackupColumn> columns,
        IDatabaseProvider sourceProvider,
        CancellationToken cancellationToken = default)
    {
        var createTableScript = await sourceProvider.GetCreateTableScriptAsync(tableName, cancellationToken);
        if (string.IsNullOrWhiteSpace(createTableScript))
        {
            await base.CreateTableLikeAsync(tableName, columns, sourceProvider, cancellationToken);
            return;
        }

        await ExecuteAsync(RewriteCreateTableName(createTableScript, tableName), cancellationToken);
    }

    public override Task DropTableIfExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync($"DROP TABLE IF EXISTS {BuildTableReference(tableName)}", cancellationToken);
    }

    public override Task DisableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync("SET FOREIGN_KEY_CHECKS = 0", cancellationToken);
    }

    public override Task EnableForeignKeysAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync("SET FOREIGN_KEY_CHECKS = 1", cancellationToken);
    }

    protected override string QuoteIdentifier(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    protected override string GetParameterName(int index) => $"@p{index}";
    protected override string BuildTableReference(string tableName) => QuoteIdentifier(tableName);

    private string RewriteCreateTableName(string createTableScript, string tableName)
    {
        return Regex.Replace(
            createTableScript,
            @"^\s*CREATE\s+TABLE\s+(`[^`]+`|\S+)",
            $"CREATE TABLE {BuildTableReference(tableName)}",
            RegexOptions.IgnoreCase);
    }

    protected override string MapStoreType(string storeType)
    {
        var normalized = storeType.ToLowerInvariant();
        return normalized switch
        {
            "int" or "integer" => "INT",
            "bigint" => "BIGINT",
            "smallint" => "SMALLINT",
            "tinyint" or "bit" => "TINYINT",
            "float" => "FLOAT",
            "double" => "DOUBLE",
            "decimal" or "numeric" => "DECIMAL(38,10)",
            "date" => "DATE",
            "time" => "TIME",
            "datetime" or "timestamp" => "DATETIME",
            "binary" or "varbinary" or "blob" or "longblob" => "LONGBLOB",
            "json" => "JSON",
            _ => "LONGTEXT"
        };
    }
}

public sealed class OracleBackupProvider : DbBackupProviderBase
{
    public override DatabaseType DatabaseType => DatabaseType.Oracle;

    protected override DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString);

    public override async Task<IReadOnlyList<string>> GetTableNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = Connection!.CreateCommand();
        command.CommandText = "SELECT TABLE_NAME FROM USER_TABLES ORDER BY TABLE_NAME";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public override async Task DropTableIfExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            await ExecuteAsync($"DROP TABLE {BuildTableReference(tableName)} CASCADE CONSTRAINTS", cancellationToken);
        }
        catch (OracleException exception) when (exception.Number == 942)
        {
            // Oracle does not support DROP TABLE IF EXISTS. Missing tables are harmless here.
        }
    }

    protected override string QuoteIdentifier(string identifier) => "\"" + identifier.ToUpperInvariant().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    protected override string GetParameterName(int index) => $":p{index}";
    protected override string BuildTableReference(string tableName) => QuoteIdentifier(tableName);

    protected override string MapStoreType(string storeType)
    {
        var normalized = storeType.ToUpperInvariant();
        return normalized switch
        {
            "NUMBER" or "INT" or "INTEGER" => "NUMBER",
            "FLOAT" => "FLOAT",
            "DATE" => "DATE",
            "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => "TIMESTAMP",
            "BLOB" or "RAW" or "LONG RAW" => "BLOB",
            "CLOB" or "NCLOB" or "XMLTYPE" => "CLOB",
            _ => "CLOB"
        };
    }
}
