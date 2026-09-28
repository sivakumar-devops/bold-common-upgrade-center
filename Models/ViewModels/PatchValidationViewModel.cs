using Bold.UpgradeCenter.Models;

namespace Bold.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model returned to the Custom Patch tab after an AJAX validate call.
    /// </summary>
    public class PatchValidationViewModel
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ParsedVersion { get; set; }
        public string? ImageRepository { get; set; }
        public string ImageReference { get; set; } = string.Empty;
        public List<PatchImageValidationItem> Images { get; set; } = [];
    }
}
