using System.Collections.Concurrent;

namespace Bold.UpgradeCenter.Services;

public interface IUpgradeJobExecutionRegistry
{
    bool TryBegin(string jobId, string owner);

    bool IsActive(string jobId);

    void Complete(string jobId, string owner);
}

public sealed class UpgradeJobExecutionRegistry : IUpgradeJobExecutionRegistry
{
    private readonly ConcurrentDictionary<string, string> activeJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<UpgradeJobExecutionRegistry> logger;

    public UpgradeJobExecutionRegistry(ILogger<UpgradeJobExecutionRegistry> logger)
    {
        this.logger = logger;
    }

    public bool TryBegin(string jobId, string owner)
    {
        if (activeJobs.TryAdd(jobId, owner))
        {
            return true;
        }

        logger.LogInformation(
            "Upgrade job execution skipped because an active runner already owns the job. JobId: {JobId}. RequestedOwner: {RequestedOwner}. ActiveOwner: {ActiveOwner}.",
            jobId,
            owner,
            activeJobs.TryGetValue(jobId, out var activeOwner) ? activeOwner : "unknown");
        return false;
    }

    public bool IsActive(string jobId)
    {
        return activeJobs.ContainsKey(jobId);
    }

    public void Complete(string jobId, string owner)
    {
        if (activeJobs.TryRemove(jobId, out var activeOwner) &&
            !string.Equals(activeOwner, owner, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Upgrade job execution owner changed before completion. JobId: {JobId}. CompletingOwner: {CompletingOwner}. ActiveOwner: {ActiveOwner}.",
                jobId,
                owner,
                activeOwner);
        }
    }
}
