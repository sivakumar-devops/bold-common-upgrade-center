namespace Bold.UpgradeCenter.Services;

public interface IPlaywrightReportStore
{
    bool TrySaveFromLogs(string jobId, PlaywrightValidationMode mode, string logs);

    bool TrySaveHtml(string jobId, PlaywrightValidationMode mode, Stream htmlStream, out string? safeError);

    bool Exists(string jobId, PlaywrightValidationMode mode);

    bool TryOpenRead(string jobId, PlaywrightValidationMode mode, out Stream? stream);

    bool TryOpenRead(string jobId, PlaywrightValidationMode mode, string? relativePath, out Stream? stream, out string contentType);
}
