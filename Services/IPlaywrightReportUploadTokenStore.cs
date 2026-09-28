using System.Security.Cryptography;
using System.Text;

namespace Bold.UpgradeCenter.Services;

public interface IPlaywrightReportUploadTokenStore
{
    string Register(string jobId, PlaywrightValidationMode mode, TimeSpan ttl, string? existingToken = null);

    PlaywrightUploadAuthorization AuthorizeUpload(
        string jobId,
        PlaywrightValidationMode mode,
        PlaywrightUploadKind kind,
        string token,
        string contentHash);

    void ResetUpload(string jobId, PlaywrightValidationMode mode, PlaywrightUploadKind kind, string contentHash);

    void Revoke(string jobId, PlaywrightValidationMode mode);
}

public enum PlaywrightUploadKind
{
    Report,
    Result
}

public sealed record PlaywrightUploadAuthorization(
    bool Succeeded,
    bool IsDuplicate,
    string? FailureReason)
{
    public static PlaywrightUploadAuthorization Accepted() => new(true, false, null);

    public static PlaywrightUploadAuthorization DuplicateAccepted() => new(true, true, null);

    public static PlaywrightUploadAuthorization Failed(string failureReason) => new(false, false, failureReason);
}

public sealed class InMemoryPlaywrightReportUploadTokenStore : IPlaywrightReportUploadTokenStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<string, UploadTokenEntry> tokens = new(StringComparer.Ordinal);

    public string Register(string jobId, PlaywrightValidationMode mode, TimeSpan ttl, string? existingToken = null)
    {
        var token = string.IsNullOrWhiteSpace(existingToken)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : existingToken;
        lock (syncRoot)
        {
            PruneExpired();
            tokens[Key(jobId, mode)] = new UploadTokenEntry(
                token,
                DateTimeOffset.UtcNow.Add(ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : ttl));
        }

        return token;
    }

    public PlaywrightUploadAuthorization AuthorizeUpload(
        string jobId,
        PlaywrightValidationMode mode,
        PlaywrightUploadKind kind,
        string token,
        string contentHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(contentHash))
        {
            return PlaywrightUploadAuthorization.Failed("Upload authorization is missing.");
        }

        lock (syncRoot)
        {
            var key = Key(jobId, mode);
            if (!tokens.TryGetValue(key, out var entry))
            {
                return PlaywrightUploadAuthorization.Failed("Upload authorization is invalid or expired.");
            }

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                tokens.Remove(key);
                return PlaywrightUploadAuthorization.Failed("Upload authorization is expired.");
            }

            if (!FixedTimeEquals(entry.Token, token))
            {
                return PlaywrightUploadAuthorization.Failed("Upload authorization is invalid.");
            }

            if (entry.ContentHashes.TryGetValue(kind, out var existingHash))
            {
                return FixedTimeEquals(existingHash, contentHash)
                    ? PlaywrightUploadAuthorization.DuplicateAccepted()
                    : PlaywrightUploadAuthorization.Failed("A different upload was already received for this job and validation stage.");
            }

            entry.ContentHashes[kind] = contentHash;
            return PlaywrightUploadAuthorization.Accepted();
        }
    }

    public void Revoke(string jobId, PlaywrightValidationMode mode)
    {
        lock (syncRoot)
        {
            tokens.Remove(Key(jobId, mode));
        }
    }

    public void ResetUpload(string jobId, PlaywrightValidationMode mode, PlaywrightUploadKind kind, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        lock (syncRoot)
        {
            if (tokens.TryGetValue(Key(jobId, mode), out var entry) &&
                entry.ContentHashes.TryGetValue(kind, out var existingHash) &&
                FixedTimeEquals(existingHash, contentHash))
            {
                entry.ContentHashes.Remove(kind);
            }
        }
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expiredKey in tokens
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToList())
        {
            tokens.Remove(expiredKey);
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string Key(string jobId, PlaywrightValidationMode mode)
    {
        return $"{jobId.Trim()}:{mode}";
    }

    private sealed record UploadTokenEntry(
        string Token,
        DateTimeOffset ExpiresAt)
    {
        public Dictionary<PlaywrightUploadKind, string> ContentHashes { get; } = new();
    }
}
