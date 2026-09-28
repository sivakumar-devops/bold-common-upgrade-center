namespace Bold.UpgradeCenter.Services;

public interface IPlaywrightRunnerImageProvider
{
    Task<string?> GetRunnerImageAsync(string selectedVersion, CancellationToken cancellationToken = default);
}
