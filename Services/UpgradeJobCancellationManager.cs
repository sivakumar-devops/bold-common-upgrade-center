using System.Collections.Concurrent;

namespace Bold.UpgradeCenter.Services;

public interface IUpgradeJobCancellationManager
{
    CancellationToken Register(string jobId);

    bool RequestCancellation(string jobId);

    void Complete(string jobId);
}

public sealed class UpgradeJobCancellationManager : IUpgradeJobCancellationManager
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> tokens = new(StringComparer.OrdinalIgnoreCase);

    public CancellationToken Register(string jobId)
    {
        var source = tokens.GetOrAdd(jobId, _ => new CancellationTokenSource());
        return source.Token;
    }

    public bool RequestCancellation(string jobId)
    {
        if (!tokens.TryGetValue(jobId, out var source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    public void Complete(string jobId)
    {
        if (!tokens.TryRemove(jobId, out var source))
        {
            return;
        }

        source.Dispose();
    }
}
