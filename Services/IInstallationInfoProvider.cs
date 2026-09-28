using System.Threading;
using System.Threading.Tasks;
using Bold.UpgradeCenter.Models;

namespace Bold.UpgradeCenter.Services
{
    /// <summary>
    /// Reads the current installation state from the host environment
    /// (e.g. Kubernetes deployment annotations, config file, registry, etc.).
    /// </summary>
    public interface IInstallationInfoProvider
    {
        /// <summary>
        /// Returns details about the currently running Bold BI installation.
        /// </summary>
        Task<InstallationInfo> GetInstallationInfoAsync(CancellationToken ct = default);
    }
}
