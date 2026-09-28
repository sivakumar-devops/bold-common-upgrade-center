using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class PlaywrightReportStore : IPlaywrightReportStore
{
    public const string ReportBeginMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_BEGIN";
    public const string ReportEndMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_END";

    private const string ReportFileName = "report.html";
    private static readonly Regex SafeNamePattern = new("[^a-zA-Z0-9_.-]+", RegexOptions.Compiled);

    private readonly string rootPath;
    private readonly int retentionJobCount;
    private readonly ILogger<PlaywrightReportStore> logger;
    private readonly object syncRoot = new();

    public PlaywrightReportStore(
        IWebHostEnvironment environment,
        IOptions<PlaywrightExecutionOptions> options,
        ILogger<PlaywrightReportStore> logger)
    {
        var configuredPath = options.Value.ReportStoragePath;
        rootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "PlaywrightReports")
            : ResolvePath(environment.ContentRootPath, configuredPath);
        retentionJobCount = Math.Max(1, options.Value.ReportRetentionJobCount);
        this.logger = logger;
    }

    public bool TrySaveFromLogs(string jobId, PlaywrightValidationMode mode, string logs)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return false;
        }

        try
        {
            var jobDirectory = GetJobDirectory(jobId);
            var stageDirectory = GetStageDirectory(jobId, mode);

            lock (syncRoot)
            {
                var encodedReport = ExtractEncodedBlock(logs, ReportBeginMarker, ReportEndMarker);
                if (string.IsNullOrWhiteSpace(encodedReport))
                {
                    return false;
                }

                Directory.CreateDirectory(stageDirectory);
                File.WriteAllBytes(Path.Combine(stageDirectory, ReportFileName), Convert.FromBase64String(encodedReport));
                Directory.SetLastWriteTimeUtc(jobDirectory, DateTime.UtcNow);
                PruneOldReports(jobDirectory);
            }

            logger.LogInformation(
                "Stored Playwright HTML report. JobId: {JobId}. Stage: {Stage}. Path: {ReportPath}.",
                jobId,
                ToStageSegment(mode),
                stageDirectory);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Playwright HTML report could not be stored. JobId: {JobId}. Stage: {Stage}.",
                jobId,
                ToStageSegment(mode));
            return false;
        }
    }

    public bool TrySaveHtml(string jobId, PlaywrightValidationMode mode, Stream htmlStream, out string? safeError)
    {
        safeError = null;
        try
        {
            var jobDirectory = GetJobDirectory(jobId);
            var stageDirectory = GetStageDirectory(jobId, mode);

            lock (syncRoot)
            {
                if (Directory.Exists(stageDirectory))
                {
                    Directory.Delete(stageDirectory, recursive: true);
                }

                Directory.CreateDirectory(stageDirectory);
                using var output = File.Create(Path.Combine(stageDirectory, ReportFileName));
                htmlStream.CopyTo(output);
                Directory.SetLastWriteTimeUtc(jobDirectory, DateTime.UtcNow);
                PruneOldReports(jobDirectory);
            }

            logger.LogInformation(
                "Stored uploaded self-contained Playwright HTML report. JobId: {JobId}. Stage: {Stage}. Path: {ReportPath}.",
                jobId,
                ToStageSegment(mode),
                stageDirectory);
            return true;
        }
        catch (Exception exception)
        {
            safeError = ToSafeStorageError("uploaded self-contained Playwright HTML report", exception);
            logger.LogWarning(
                exception,
                "Uploaded self-contained Playwright HTML report could not be stored. JobId: {JobId}. Stage: {Stage}.",
                jobId,
                ToStageSegment(mode));
            return false;
        }
    }

    public bool Exists(string jobId, PlaywrightValidationMode mode)
    {
        return File.Exists(GetReportPath(jobId, mode));
    }

    public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, out Stream? stream)
    {
        return TryOpenRead(jobId, mode, null, out stream, out _);
    }

    public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, string? relativePath, out Stream? stream, out string contentType)
    {
        stream = null;
        contentType = "application/octet-stream";

        var stageDirectory = Path.GetFullPath(GetStageDirectory(jobId, mode));
        var requestedPath = string.IsNullOrWhiteSpace(relativePath)
            ? ReportFileName
            : relativePath.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(requestedPath) || requestedPath.Split('/').Any(segment => segment == ".."))
        {
            return false;
        }

        var reportPath = Path.GetFullPath(Path.Combine(stageDirectory, requestedPath));
        if (!reportPath.StartsWith(stageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(reportPath, stageDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(reportPath))
        {
            return false;
        }

        contentType = GetContentType(reportPath);
        stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return true;
    }

    private static string ResolvePath(string contentRootPath, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    private static string ExtractEncodedBlock(string logs, string beginMarker, string endMarker)
    {
        var begin = logs.LastIndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return string.Empty;
        }

        begin += beginMarker.Length;
        var end = logs.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            return string.Empty;
        }

        return logs[begin..end]
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private string GetReportPath(string jobId, PlaywrightValidationMode mode)
    {
        return Path.Combine(GetStageDirectory(jobId, mode), ReportFileName);
    }

    private string GetStageDirectory(string jobId, PlaywrightValidationMode mode)
    {
        var stage = ToStageSegment(mode);
        return Path.Combine(GetJobDirectory(jobId), stage);
    }

    private string GetJobDirectory(string jobId)
    {
        return Path.Combine(rootPath, SafeSegment(jobId));
    }

    private void PruneOldReports(string currentJobDirectory)
    {
        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
        {
            return;
        }

        var currentRoot = Path.GetFullPath(currentJobDirectory);
        var jobs = root.GetDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();

        foreach (var directory in jobs.Skip(retentionJobCount))
        {
            var fullName = Path.GetFullPath(directory.FullName);
            if (string.Equals(fullName, currentRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                directory.Delete(recursive: true);
                logger.LogInformation("Deleted old Playwright report directory. Path: {ReportDirectory}.", directory.FullName);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Old Playwright report directory could not be deleted. Path: {ReportDirectory}.", directory.FullName);
            }
        }
    }

    private static string SafeSegment(string value)
    {
        var safe = SafeNamePattern.Replace(value.Trim(), "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static string ToStageSegment(PlaywrightValidationMode mode)
    {
        return mode switch
        {
            PlaywrightValidationMode.Post => "post-upgrade",
            PlaywrightValidationMode.Cleanup => "cleanup",
            _ => "pre-upgrade"
        };
    }

    private static string ToSafeStorageError(string subject, Exception exception)
    {
        var message = exception.Message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return $"{subject} could not be stored. {exception.GetType().Name}: {message}";
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".zip" => "application/zip",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".webm" => "video/webm",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }
}
