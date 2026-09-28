using System.Collections.Concurrent;

namespace Bold.UpgradeCenter.Services;

public sealed class InMemoryPlaywrightValidationResultStore : IPlaywrightValidationResultStore
{
    private readonly ConcurrentDictionary<string, PlaywrightValidationResultSummary> summaries = new(StringComparer.OrdinalIgnoreCase);

    public void Save(string jobId, PlaywrightValidationMode mode, PlaywrightValidationResultSummary summary)
    {
        summaries[Key(jobId, mode)] = summary;
    }

    public bool TryGet(string jobId, PlaywrightValidationMode mode, out PlaywrightValidationResultSummary? summary)
    {
        return summaries.TryGetValue(Key(jobId, mode), out summary);
    }

    private static string Key(string jobId, PlaywrightValidationMode mode)
    {
        return $"{jobId}:{mode}";
    }
}
