namespace Bold.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model for the manual rollback confirmation page.
    /// </summary>
    public class ConfirmRollbackViewModel
    {
        public UpgradeCenterUser CurrentUser { get; set; } = new();

        public string JobId { get; set; } = string.Empty;

        public string RollbackId { get; set; } = string.Empty;

        public string RollbackFromVersion { get; set; } = string.Empty;

        public string RestoreToVersion { get; set; } = string.Empty;

        public DateTime RollbackPointCreatedAt { get; set; }

        public int BackupDatabaseCount { get; set; }

        public int KubernetesImageCount { get; set; }

        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
    }
}
