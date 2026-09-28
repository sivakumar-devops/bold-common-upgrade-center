using System.Collections.Concurrent;

namespace Bold.UpgradeCenter.Security;

public sealed record AdminContinuationUser(
    string UserId,
    string UserName,
    string? Email,
    DateTimeOffset ValidatedAt);

public interface IAdminContinuationStore
{
    void Remember(string sessionFingerprint, AdminContinuationUser user);

    bool TryGet(string sessionFingerprint, out AdminContinuationUser user);

    void Clear(string sessionFingerprint);
}

public sealed class InMemoryAdminContinuationStore : IAdminContinuationStore
{
    private readonly ConcurrentDictionary<string, AdminContinuationUser> users = new(StringComparer.Ordinal);

    public void Remember(string sessionFingerprint, AdminContinuationUser user)
    {
        users[sessionFingerprint] = user;
    }

    public bool TryGet(string sessionFingerprint, out AdminContinuationUser user)
    {
        return users.TryGetValue(sessionFingerprint, out user!);
    }

    public void Clear(string sessionFingerprint)
    {
        users.TryRemove(sessionFingerprint, out _);
    }
}
