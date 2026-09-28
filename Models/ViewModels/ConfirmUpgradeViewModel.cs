namespace Bold.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model for the Confirm Upgrade modal (Screen 3).
    /// </summary>
    public class ConfirmUpgradeViewModel
    {
        public UpgradeCenterUser CurrentUser { get; set; } = new();

        public Services.UpgradeProductDefinition SelectedProduct { get; set; } = Services.UpgradeProductDefinitions.BoldBi;

        public string FromVersion { get; set; } = string.Empty;
        public string ToVersion { get; set; } = string.Empty;

        /// <summary>
        /// When true the modal shows the automatic schema-backup notice.
        /// </summary>
        public bool HasSchemaChanges { get; set; }

        /// <summary>
        /// For custom patches: the raw image reference the admin typed.
        /// Null for standard catalog releases.
        /// </summary>
        public string? CustomImageReference { get; set; }

        public ReleaseType ReleaseType { get; set; } = ReleaseType.Standard;

        public bool SchemaImpactKnown { get; set; }

        public int AffectedTableCount { get; set; }

        public IReadOnlyList<string> AffectedTables { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> DatabaseTypes { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        public bool IsStartBlocked { get; set; }

        public string? StartBlockedMessage { get; set; }
    }
}
