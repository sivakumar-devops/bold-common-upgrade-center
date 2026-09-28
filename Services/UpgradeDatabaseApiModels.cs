using System.Text.Json.Serialization;

namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeDatabaseScriptResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("hasScriptChanges")]
    public bool HasScriptChanges { get; set; }

    [JsonPropertyName("scriptQuery")]
    public string? ScriptQuery { get; set; }

    [JsonPropertyName("hasSchemaChanges")]
    public bool HasSchemaChanges { get; set; }

    [JsonPropertyName("components")]
    public IReadOnlyList<UpgradeDatabaseScriptComponent> Components { get; set; } = [];
}

public sealed class UpgradeDatabaseScriptComponent
{
    [JsonPropertyName("component")]
    public string Component { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("hasSchemaChanges")]
    public bool HasSchemaChanges { get; set; }

    [JsonPropertyName("defaultTablePrefix")]
    public string DefaultTablePrefix { get; set; } = string.Empty;

    [JsonPropertyName("sourceScriptVersion")]
    public string SourceScriptVersion { get; set; } = string.Empty;

    [JsonPropertyName("changes")]
    public IReadOnlyList<UpgradeDatabaseScriptTableChange> Changes { get; set; } = [];
}

public sealed class UpgradeDatabaseScriptTableChange
{
    [JsonPropertyName("changeType")]
    public string ChangeType { get; set; } = string.Empty;

    [JsonPropertyName("schemaName")]
    public string? SchemaName { get; set; }

    [JsonPropertyName("sourcePrefix")]
    public string? SourcePrefix { get; set; }

    [JsonPropertyName("baseTableName")]
    public string BaseTableName { get; set; } = string.Empty;

    [JsonPropertyName("logicalTableName")]
    public string LogicalTableName { get; set; } = string.Empty;
}
