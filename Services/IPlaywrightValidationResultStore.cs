namespace Bold.UpgradeCenter.Services;

public interface IPlaywrightValidationResultStore
{
    void Save(string jobId, PlaywrightValidationMode mode, PlaywrightValidationResultSummary summary);

    bool TryGet(string jobId, PlaywrightValidationMode mode, out PlaywrightValidationResultSummary? summary);
}

public sealed record PlaywrightValidationResultSummary(
    string Mode,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double PassPercentage,
    int Threshold,
    string Status,
    int? CommandExitCode,
    string? Reason,
    IReadOnlyList<PlaywrightFailedTestSummary>? FailedTests)
{
    public string ToMessage(string prefix)
    {
        if (string.Equals(Mode, "cleanup", StringComparison.OrdinalIgnoreCase))
        {
            return ToCleanupMessage();
        }

        var itemName = "tests";
        var label = "Validation";
        var skippedText = Skipped > 0 ? $", and {Skipped} were skipped" : string.Empty;
        var failedText = Failed > 0 ? $", {Failed} failed" : string.Empty;

        if (!string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) &&
            Failed == 0 &&
            PassPercentage < Threshold)
        {
            return $"{label} did not meet the required pass threshold. {Passed} of {Total} {itemName} passed ({PassPercentage:0.##}%; required: {Threshold}%){skippedText}.";
        }

        if (!string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) && Failed > 0)
        {
            return $"{label} failed. {Passed} of {Total} {itemName} passed{failedText}{skippedText}. Pass rate: {PassPercentage:0.##}%; required: {Threshold}%.";
        }

        return $"{prefix} {Passed} of {Total} {itemName} passed ({PassPercentage:0.##}%; required: {Threshold}%){failedText}{skippedText}.";
    }

    private string ToCleanupMessage()
    {
        if (string.Equals(Status, "Passed", StringComparison.OrdinalIgnoreCase) &&
            Total > 0 &&
            Failed == 0 &&
            Passed + Skipped == Total)
        {
            return "Cleanup completed successfully. All validation resources were removed.";
        }

        return "Cleanup could not be completed. Manual cleanup is required.";
    }
}

public sealed record PlaywrightFailedTestSummary(
    string Name,
    string? ClassName,
    string? Message);
