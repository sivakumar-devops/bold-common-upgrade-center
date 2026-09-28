using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bold.UpgradeCenter.Models;
using UiUpgradeJob = Bold.UpgradeCenter.Models.UpgradeJob;

namespace Bold.UpgradeCenter.Services
{
    /// <summary>
    /// Core upgrade orchestration service.
    /// All write operations are fire-and-track: they start a background job and
    /// return the job-id immediately so the UI can poll via IUpgradeJobStore.
    /// </summary>
    public interface IUpgradeService
    {
        // Catalog.

        /// <summary>
        /// Returns ordered list of releases available for upgrade from the
        /// currently installed version, newest first.
        /// Business logic: fetch catalog, compute upgrade paths, mark
        /// recommended / blocked entries.
        /// </summary>
        Task<List<Release>> GetAvailableReleasesAsync(
            string installedVersion,
            CancellationToken ct = default);

        // Patch validation.

        /// <summary>
        /// Validates generated custom patch images from a patch version and
        /// optional repository override.
        /// </summary>
        Task<PatchValidationResult> ValidatePatchImageAsync(
            string patchVersion,
            string? imageRepository = null,
            CancellationToken ct = default);

        Task<string?> GetDefaultCustomPatchRepositoryAsync(
            CancellationToken ct = default);

        // Upgrade execution.

        /// <summary>
        /// Starts an upgrade job for a standard catalog release.
        /// Returns the new UpgradeJob with status = Running.
        /// </summary>
        Task<UiUpgradeJob> StartUpgradeAsync(
            string targetVersion,
            string initiatedBy,
            CancellationToken ct = default);

        /// <summary>
        /// Starts an upgrade job for generated custom patch images.
        /// Returns the new UpgradeJob with status = Running.
        /// </summary>
        Task<UiUpgradeJob> StartCustomPatchUpgradeAsync(
            string patchVersion,
            string? imageRepository,
            string initiatedBy,
            CancellationToken ct = default);


        Task<UiUpgradeJob> CancelUpgradeAsync(
            string jobId,
            string requestedBy,
            CancellationToken ct = default);
        // Rollback.

        /// <summary>
        /// Triggers a rollback of the specified completed upgrade job.
        /// Returns the new rollback UpgradeJob with status = Running.
        /// </summary>
        Task<UiUpgradeJob> StartRollbackAsync(
            string upgradeJobId,
            string initiatedBy,
            CancellationToken ct = default);
    }
}
