using System.Collections.Generic;
using Bold.UpgradeCenter.Services;

namespace Bold.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model for the Upgrade Center dashboard (Screen 2).
    /// Contains version cards, available releases, history, and optional
    /// in-progress job banner.
    /// </summary>
    public class DashboardViewModel
    {
        public UpgradeCenterUser CurrentUser { get; set; } = new();
        public InstallationInfo Installation { get; set; } = new();

        public UpgradeProductDefinition SelectedProduct { get; set; } = UpgradeProductDefinitions.BoldBi;

        public IReadOnlyList<UpgradeProductDefinition> AvailableProducts { get; set; } = UpgradeProductDefinitions.All;

        public UpgradeDeploymentMode DeploymentMode { get; set; } = UpgradeDeploymentMode.Common;

        public string DeploymentModeSource { get; set; } = string.Empty;

        // Available releases tab.
        public List<Release> AvailableReleases { get; set; } = new();

        // History tab.
        public List<UpgradeHistoryEntry> History { get; set; } = new();

        // In-progress banner.
        /// <summary>
        /// When an upgrade or rollback job is currently running this is set
        /// so the amber "in progress" banner is shown at the top of the page.
        /// </summary>
        public UpgradeJob? ActiveJob { get; set; }

        /// <summary>
        /// True when any job is currently in flight. The dashboard uses this
        /// to disable the Upgrade buttons so a second job cannot be queued.
        /// </summary>
        public bool HasActiveJob => ActiveJob != null;

        /// <summary>
        /// The most recent successful upgrade that is still within its rollback
        /// window: the only job the user is allowed to roll back from here.
        /// Null when nothing is currently rollbackable.
        /// </summary>
        public UpgradeJob? RollbackableJob { get; set; }
    }
}
