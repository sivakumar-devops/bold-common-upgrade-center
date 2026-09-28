using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Bold.UpgradeCenter.Models;

namespace Bold.UpgradeCenter.Services
{
    /// <summary>
    /// Determines whether the current HTTP user is allowed to use the
    /// Upgrade Center (must be a UMS Admin).
    /// </summary>
    public interface IUpgradeCenterAuthorizationService
    {
        /// <summary>
        /// Returns a populated <see cref="UpgradeCenterUser"/> for the given
        /// claims principal.  IsAuthorized will be false for non-admin users.
        /// </summary>
        Task<UpgradeCenterUser> GetCurrentUserAsync(
            ClaimsPrincipal principal,
            CancellationToken ct = default);
    }
}
