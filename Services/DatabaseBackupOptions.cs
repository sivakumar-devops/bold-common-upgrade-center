namespace Bold.UpgradeCenter.Services;

public sealed class DatabaseBackupOptions
{
    public int BatchSize { get; set; } = 1000;

    public bool DropAndRecreate { get; set; } = true;

    public bool DisableForeignKeysDuringLoad { get; set; } = true;
}
