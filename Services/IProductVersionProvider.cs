namespace Bold.UpgradeCenter.Services;

public interface IProductVersionProvider
{
    Task<ProductVersionInfo> GetCurrentVersionAsync(CancellationToken cancellationToken = default);
}

public sealed record ProductVersionInfo(string ProductName, string Version, string Source = "Configuration");
