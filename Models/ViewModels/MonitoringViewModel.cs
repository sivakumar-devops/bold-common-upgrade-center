namespace Bold.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model for the Upgrade Monitoring page. The same view is reused
    /// for upgrade and rollback jobs; the job status drives the banner and rail.
    /// </summary>
    public class MonitoringViewModel
    {
        public UpgradeCenterUser CurrentUser { get; set; } = new();

        public UpgradeJob Job { get; set; } = new();

        public bool CanCancelUpgrade { get; set; }

        public string Phase1Label { get; set; } = "Schema Backup";

        public string Phase2Label { get; set; } = "Pre-upgrade";

        public string Phase3Label { get; set; } = "Upgrade";

        public string Phase4Label { get; set; } = "Post-upgrade";

        public IReadOnlyList<string> PhaseLabels =>
            Job.IsRollback
                ? new[] { Phase1Label, Phase2Label, Phase3Label }
                : new[] { Phase1Label, Phase2Label, Phase3Label, Phase4Label };
    }
}
