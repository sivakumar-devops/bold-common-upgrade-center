namespace Bold.UpgradeCenter.Services;

public interface IReleaseVersionProvider
{
    Task<ReleaseVersionSummary> GetReleaseVersionsAsync(string currentVersion, CancellationToken cancellationToken = default);
}

public sealed record ReleaseVersionSummary(
    IReadOnlyList<ReleaseVersionInfo> Versions,
    ReleaseVersionInfo? LatestVersion,
    VersionComparisonState ComparisonState,
    string? ErrorMessage);

public sealed record ReleaseVersionInfo(
    string Version,
    string TagName,
    string? Name,
    string Url,
    DateTimeOffset? PublishedAt);

public enum VersionComparisonState
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    CurrentNewerThanAvailable
}
