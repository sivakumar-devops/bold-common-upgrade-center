using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Bold.UpgradeCenter.Models;
using Bold.UpgradeCenter.Models.ViewModels;
using Bold.UpgradeCenter.Security;
using Bold.UpgradeCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UiUpgradeJob = Bold.UpgradeCenter.Models.UpgradeJob;

namespace Bold.UpgradeCenter.Controllers
{
    /// <summary>
    /// Handles all Upgrade Center page navigation and user-initiated actions.
    ///
    /// Route prefix: /upgrade-center
    /// </summary>
    [Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
    [Route("upgrade-center")]
    public class UpgradeCenterController : Controller
    {
        private const int CompleteLogDisplayMessageLimit = 4000;

        private readonly IUpgradeService _upgradeService;
        private readonly IUpgradeJobStore _jobStore;
        private readonly IInstallationInfoProvider _installationInfo;
        private readonly IUpgradeCenterAuthorizationService _authService;
        private readonly IUpgradeCenterConfigurationProvider _configurationProvider;
        private readonly IUpgradeImpactService _upgradeImpactService;
        private readonly IUpgradeHistoryStore _historyStore;
        private readonly IUpgradeRollbackStore _rollbackStore;
        private readonly IPlaywrightReportStore _playwrightReportStore;
        private readonly IPlaywrightReportUploadTokenStore _playwrightReportUploadTokenStore;
        private readonly IPlaywrightValidationResultStore _playwrightValidationResultStore;
        private readonly IUpgradeOperationLogStore _operationLogStore;
        private readonly IUpgradeEnvironmentEligibilityService _upgradeEnvironmentEligibilityService;
        private readonly IUpgradeProductContext _productContext;
        private readonly IUpgradeDeploymentModeService _deploymentModeService;
        private readonly IOptions<UpgradeCenterOptions> _options;
        private readonly ILogger<UpgradeCenterController> _logger;

        public UpgradeCenterController(
            IUpgradeService upgradeService,
            IUpgradeJobStore jobStore,
            IInstallationInfoProvider installationInfo,
            IUpgradeCenterAuthorizationService authService,
            IUpgradeCenterConfigurationProvider configurationProvider,
            IUpgradeImpactService upgradeImpactService,
            IUpgradeHistoryStore historyStore,
            IUpgradeRollbackStore rollbackStore,
            IPlaywrightReportStore playwrightReportStore,
            IPlaywrightReportUploadTokenStore playwrightReportUploadTokenStore,
            IPlaywrightValidationResultStore playwrightValidationResultStore,
            IUpgradeOperationLogStore operationLogStore,
            IUpgradeEnvironmentEligibilityService upgradeEnvironmentEligibilityService,
            IUpgradeProductContext productContext,
            IUpgradeDeploymentModeService deploymentModeService,
            IOptions<UpgradeCenterOptions> options,
            ILogger<UpgradeCenterController> logger)
        {
            _upgradeService  = upgradeService;
            _jobStore        = jobStore;
            _installationInfo = installationInfo;
            _authService     = authService;
            _configurationProvider = configurationProvider;
            _upgradeImpactService = upgradeImpactService;
            _historyStore = historyStore;
            _rollbackStore = rollbackStore;
            _playwrightReportStore = playwrightReportStore;
            _playwrightReportUploadTokenStore = playwrightReportUploadTokenStore;
            _playwrightValidationResultStore = playwrightValidationResultStore;
            _operationLogStore = operationLogStore;
            _upgradeEnvironmentEligibilityService = upgradeEnvironmentEligibilityService;
            _productContext = productContext;
            _deploymentModeService = deploymentModeService;
            _options = options;
            _logger = logger;
        }

        // GET /upgrade-center: Dashboard (Screen 2) or Unauthorized (Screen 1).

        [HttpGet("")]
        [HttpGet("index")]
        public async Task<IActionResult> Index(string? product)
        {
            var user = await _authService.GetCurrentUserAsync(User);

            if (!user.IsAuthorized)
                return View("Unauthorized", user);

            var productSelection = await ResolveProductSelectionAsync(product, HttpContext.RequestAborted);
            var selectedProduct = productSelection.SelectedProduct;
            if (!string.IsNullOrWhiteSpace(product) &&
                !string.Equals(productSelection.RequestedProduct.Key, selectedProduct.Key, StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
            }

            var installation = await _installationInfo.GetInstallationInfoAsync();
            var releases     = await _upgradeService.GetAvailableReleasesAsync(installation.InstalledVersion);
            var history      = await GetHistoryEntriesForDashboardAsync(selectedProduct, HttpContext.RequestAborted);
            var activeJob    = ResolveActiveJob();
            var rollbackable = ResolveMostRecentRollbackableJob(selectedProduct.Key);

            var vm = new DashboardViewModel
            {
                CurrentUser        = user,
                Installation       = installation,
                SelectedProduct    = selectedProduct,
                AvailableProducts  = productSelection.AvailableProducts,
                DeploymentMode     = productSelection.Mode.Mode,
                DeploymentModeSource = productSelection.Mode.Source,
                AvailableReleases  = releases,
                History            = history,
                ActiveJob          = activeJob,
                RollbackableJob    = rollbackable
            };

            // Pass an error/success banner message through from a TempData entry.
            if (TempData["ErrorMessage"] is string err)
            {
                ViewData["ErrorMessage"] = err;
            }
            else if (!string.IsNullOrWhiteSpace(productSelection.Mode.WarningMessage))
            {
                ViewData["ErrorTitle"] = "Deployment mode auto-detection";
                ViewData["ErrorMessage"] = productSelection.Mode.WarningMessage;
            }
            else if (ViewData["DashboardWarning"] is string warning)
            {
                ViewData["ErrorTitle"] = "Information temporarily unavailable";
                ViewData["ErrorMessage"] = warning;
            }

            return View("Dashboard", vm);
        }

        [AllowAnonymous]
        [HttpGet("unauthorized")]
        public async Task<IActionResult> UnauthorizedPage()
        {
            var user = await _authService.GetCurrentUserAsync(User);
            var runtimeConfiguration = await _configurationProvider.GetConfigurationAsync(HttpContext.RequestAborted);
            ViewData["ReturnToBoldBiUrl"] = string.IsNullOrWhiteSpace(runtimeConfiguration.IdpBaseUrl)
                ? "/"
                : runtimeConfiguration.IdpBaseUrl;

            return View("Unauthorized", user);
        }

        [HttpGet("logout")]
        public async Task<IActionResult> Logout(CancellationToken cancellationToken)
        {
            var runtimeConfiguration = await _configurationProvider.GetConfigurationAsync(cancellationToken);
            var idpBaseUrl = runtimeConfiguration.IdpBaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(idpBaseUrl))
            {
                return RedirectToAction(nameof(UnauthorizedPage));
            }

            var loginPath = string.IsNullOrWhiteSpace(_options.Value.Authentication.LoginPath)
                ? "/accounts/login"
                : _options.Value.Authentication.LoginPath;
            var logoutUrl = CombineUrl(idpBaseUrl, "accounts/logout");
            var returnUrl = CombineUrl(idpBaseUrl, loginPath);
            Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            Response.Headers.Pragma = "no-cache";
            return Redirect($"{logoutUrl}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        // GET /upgrade-center/confirm?version=12.4.0: Confirm modal (Screen 3).

        [HttpGet("confirm")]
        public async Task<IActionResult> Confirm(
            string? version,
            string? imageRef,
            string? product)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);
            var productSelection = await ResolveProductSelectionAsync(product, HttpContext.RequestAborted);
            var selectedProduct = productSelection.SelectedProduct;
            if (!string.Equals(productSelection.RequestedProduct.Key, selectedProduct.Key, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = $"{productSelection.RequestedProduct.DisplayName} upgrade is not available for this deployment.";
                return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
            }

            var installation = await _installationInfo.GetInstallationInfoAsync();

            // Determine what we are upgrading to
            Release? release = null;
            if (!string.IsNullOrWhiteSpace(version))
            {
                var releases = await _upgradeService.GetAvailableReleasesAsync(installation.InstalledVersion);
                release = releases.Find(r => r.Version == version);
            }

            var targetVersion = release?.Version ?? version ?? imageRef ?? "—";
            UpgradeImpactResult? impact = null;
            if (string.IsNullOrWhiteSpace(imageRef) &&
                !string.IsNullOrWhiteSpace(targetVersion) &&
                targetVersion != "—")
            {
                impact = await _upgradeImpactService.GetImpactAsync(targetVersion, HttpContext.RequestAborted);
            }

            var eligibility = await _upgradeEnvironmentEligibilityService.CheckAsync(HttpContext.RequestAborted);
            var vm = new ConfirmUpgradeViewModel
            {
                CurrentUser          = user,
                SelectedProduct      = selectedProduct,
                FromVersion          = installation.InstalledVersion,
                ToVersion            = targetVersion,
                HasSchemaChanges     = impact?.DatabaseSchemaChangesExpected ?? release?.HasSchemaChanges ?? false,
                CustomImageReference = imageRef,
                ReleaseType          = string.IsNullOrWhiteSpace(imageRef)
                                           ? ReleaseType.Standard
                                           : ReleaseType.CustomPatch,
                SchemaImpactKnown    = impact?.SchemaImpactKnown ?? false,
                AffectedTableCount   = impact?.AffectedTableCount ?? 0,
                AffectedTables       = impact?.AffectedTables ?? Array.Empty<string>(),
                DatabaseTypes        = impact?.DatabaseTypes ?? Array.Empty<string>(),
                Warnings             = impact?.Warnings ?? Array.Empty<string>(),
                IsStartBlocked       = !eligibility.IsSupported,
                StartBlockedMessage  = eligibility.WarningMessage
            };

            return View("Confirm", vm);
        }

        // POST /upgrade-center/start: Start an upgrade job.

        [HttpPost("start")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartUpgrade(
            string? version,
            string? imageRef,
            string? patchVersion,
            string? imageRepository,
            string? product)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);
            var productSelection = await ResolveProductSelectionAsync(product, HttpContext.RequestAborted);
            var selectedProduct = productSelection.SelectedProduct;
            if (!string.Equals(productSelection.RequestedProduct.Key, selectedProduct.Key, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = $"{productSelection.RequestedProduct.DisplayName} upgrade is not available for this deployment.";
                return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
            }

            // Block new upgrade if any job is already running (single-job constraint).
            var activeJob = ResolveActiveJob();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Upgrade Center runs one operation at a time because Bold BI and Bold Reports can share IDP, UMS, ETL, and database resources. " +
                    "Please wait until it finishes before starting a new one.";
                return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
            }

            UiUpgradeJob job;

            var requestedPatchVersion = !string.IsNullOrWhiteSpace(patchVersion) ? patchVersion : imageRef;
            if (!string.IsNullOrWhiteSpace(requestedPatchVersion))
            {
                try
                {
                    await _upgradeEnvironmentEligibilityService.EnsureUpgradeSupportedAsync(HttpContext.RequestAborted);
                }
                catch (InvalidOperationException exception)
                {
                    TempData["PatchError"] = exception.Message;
                    return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
                }

                var validation = await _upgradeService.ValidatePatchImageAsync(requestedPatchVersion, imageRepository);
                if (!validation.IsValid)
                {
                    TempData["PatchError"] = validation.ErrorMessage;
                    return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
                }

                job = await _upgradeService.StartCustomPatchUpgradeAsync(requestedPatchVersion, imageRepository, user.DisplayName);
            }
            else if (!string.IsNullOrWhiteSpace(version))
            {
                try
                {
                    await _upgradeEnvironmentEligibilityService.EnsureUpgradeSupportedAsync(HttpContext.RequestAborted);
                }
                catch (InvalidOperationException exception)
                {
                    TempData["ErrorMessage"] = exception.Message;
                return RedirectToAction(nameof(Index), new { product = selectedProduct.Key });
                }

                job = await _upgradeService.StartUpgradeAsync(version, user.DisplayName);
            }
            else
            {
                return BadRequest("Either 'version' or 'imageRef' must be supplied.");
            }

            return RedirectToAction(nameof(Monitor), new { jobId = job.JobId });
        }

        // GET /upgrade-center/monitor/{jobId}: Monitoring page (Screens 4 / 5 / 6).

        [HttpGet("monitor/{jobId}")]
        public async Task<IActionResult> Monitor(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            if (!_jobStore.TryGet(jobId, out var backendJob)) return NotFound($"Job '{jobId}' not found.");
            _productContext.SetCurrent(backendJob.ProductKey);

            var job = UpgradeJobViewModelMapper.ToViewModel(backendJob);
            ApplyPlaywrightReportLinks(job);
            ApplyCompleteOperationLogs(job);
            var vm = BuildMonitoringViewModel(user, job);
            vm.CanCancelUpgrade = CanCancelUpgrade(backendJob);
            await ApplyRollbackEligibilityAsync(job);
            return View("Monitor", vm);
        }

        // GET /upgrade-center/monitor/{jobId}/status: AJAX polling endpoint
        // Returns a partial view so the page can refresh just the job content.

        [HttpGet("monitor/{jobId}/status")]
        public async Task<IActionResult> MonitorStatus(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            if (!_jobStore.TryGet(jobId, out var backendJob)) return NotFound();
            _productContext.SetCurrent(backendJob.ProductKey);

            var job = UpgradeJobViewModelMapper.ToViewModel(backendJob);
            ApplyPlaywrightReportLinks(job);
            ApplyCompleteOperationLogs(job);
            var vm = BuildMonitoringViewModel(user, job);
            vm.CanCancelUpgrade = CanCancelUpgrade(backendJob);
            await ApplyRollbackEligibilityAsync(job);
            return PartialView("_MonitoringContent", vm);
        }

        [HttpPost("monitor/{jobId}/cancel")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelUpgrade(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            try
            {
                await _upgradeService.CancelUpgradeAsync(jobId, user.DisplayName);
                TempData["InfoMessage"] = "Upgrade cancellation was requested. The monitoring page will update as the current stage stops safely.";
            }
            catch (InvalidOperationException exception)
            {
                TempData["ErrorMessage"] = exception.Message;
            }

            return RedirectToAction(nameof(Monitor), new { jobId });
        }

        [HttpGet("monitor/{jobId}/logs")]
        public async Task<IActionResult> MonitorLogs(string jobId, [FromQuery] long afterSequence = 0)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            if (!_jobStore.TryGet(jobId, out var backendJob)) return NotFound();

            var entries = _operationLogStore.ListAfter(jobId, afterSequence, 250);
            return Json(new
            {
                isTerminal = backendJob.Status is Services.UpgradeJobStatus.Succeeded
                    or Services.UpgradeJobStatus.Failed
                    or Services.UpgradeJobStatus.Cancelled
                    or Services.UpgradeJobStatus.RollbackSucceeded
                    or Services.UpgradeJobStatus.RollbackFailed,
                entries = entries.Select(entry => ToLogLine(entry, truncateForDisplay: true))
            });
        }

        [HttpGet("monitor/{jobId}/logs/download")]
        public async Task<IActionResult> DownloadCompleteLogs(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            if (!_jobStore.TryGet(jobId, out _)) return NotFound();

            var entries = _operationLogStore.ListAll(jobId);
            var content = entries.Count == 0
                ? $"No complete operation logs are available for job {jobId}.{Environment.NewLine}"
                : string.Join(Environment.NewLine + Environment.NewLine, entries.Select(FormatLogLineForDownload));
            var fileName = $"upgrade-job-{jobId}-complete-logs.txt";
            return File(Encoding.UTF8.GetBytes(content), "text/plain; charset=utf-8", fileName);
        }

        [HttpGet("reports/{jobId}/{stage}/html")]
        [HttpGet("reports/{jobId}/{stage}/html/{**assetPath}")]
        public async Task<IActionResult> PlaywrightHtmlReport(string jobId, string stage, string? assetPath = null)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            if (!_jobStore.TryGet(jobId, out _))
            {
                return NotFound();
            }

            if (!TryParsePlaywrightStage(stage, out var mode))
            {
                return NotFound();
            }

            if (!_playwrightReportStore.TryOpenRead(jobId, mode, assetPath, out var stream, out var contentType) || stream is null)
            {
                return NotFound();
            }

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, contentType);
        }

        [AllowAnonymous]
        [HttpPost("api/playwright-reports/{jobId}/{stage}")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> UploadPlaywrightReport(string jobId, string stage)
        {
            if (!_jobStore.TryGet(jobId, out _))
            {
                return NotFound();
            }

            if (!TryParsePlaywrightStage(stage, out var mode))
            {
                return NotFound();
            }

            await using var reportBuffer = new MemoryStream();
            await Request.Body.CopyToAsync(reportBuffer, HttpContext.RequestAborted);
            reportBuffer.Position = 0;
            var reportHash = ComputeSha256Hash(reportBuffer);
            var uploadAuthorization = _playwrightReportUploadTokenStore.AuthorizeUpload(
                jobId,
                mode,
                PlaywrightUploadKind.Report,
                ResolveUploadToken(),
                reportHash);
            reportBuffer.Position = 0;
            if (!uploadAuthorization.Succeeded)
            {
                return ToUploadAuthorizationFailure(uploadAuthorization);
            }

            if (uploadAuthorization.IsDuplicate)
            {
                return Ok(new { saved = true, duplicate = true });
            }

            string? safeError;
            var saved = _playwrightReportStore.TrySaveHtml(jobId, mode, reportBuffer, out safeError);
            if (!saved)
            {
                _playwrightReportUploadTokenStore.ResetUpload(jobId, mode, PlaywrightUploadKind.Report, reportHash);
            }

            var stageName = ToPlaywrightStageDisplayName(mode);

            _operationLogStore.Append(
                jobId,
                stageName,
                saved ? "Info" : "Warning",
                "PlaywrightReport",
                saved
                    ? "Playwright HTML report saved."
                    : $"Playwright HTML report upload was received but could not be stored. {safeError ?? "No additional storage details were available."}");

            return saved
                ? Created(string.Empty, new { saved = true })
                : BadRequest(new { saved = false, error = safeError });
        }

        [AllowAnonymous]
        [HttpPost("api/playwright-results/{jobId}/{stage}")]
        public async Task<IActionResult> UploadPlaywrightResult(string jobId, string stage)
        {
            if (!_jobStore.TryGet(jobId, out _))
            {
                return NotFound();
            }

            if (!TryParsePlaywrightStage(stage, out var mode))
            {
                return NotFound();
            }

            await using var resultBuffer = new MemoryStream();
            await Request.Body.CopyToAsync(resultBuffer, HttpContext.RequestAborted);
            resultBuffer.Position = 0;
            var resultHash = ComputeSha256Hash(resultBuffer);
            var uploadAuthorization = _playwrightReportUploadTokenStore.AuthorizeUpload(
                jobId,
                mode,
                PlaywrightUploadKind.Result,
                ResolveUploadToken(),
                resultHash);
            resultBuffer.Position = 0;
            if (!uploadAuthorization.Succeeded)
            {
                return ToUploadAuthorizationFailure(uploadAuthorization);
            }

            if (uploadAuthorization.IsDuplicate)
            {
                return Ok(new { saved = true, duplicate = true });
            }

            PlaywrightValidationResultSummary? summary;
            try
            {
                summary = await JsonSerializer.DeserializeAsync<PlaywrightValidationResultSummary>(
                    resultBuffer,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    HttpContext.RequestAborted);
            }
            catch (JsonException)
            {
                _playwrightReportUploadTokenStore.ResetUpload(jobId, mode, PlaywrightUploadKind.Result, resultHash);
                return BadRequest(new { saved = false, error = "Invalid Playwright result JSON." });
            }

            if (summary is null)
            {
                _playwrightReportUploadTokenStore.ResetUpload(jobId, mode, PlaywrightUploadKind.Result, resultHash);
                return BadRequest(new { saved = false, error = "Playwright result JSON was empty." });
            }

            _playwrightValidationResultStore.Save(jobId, mode, summary);

            return Created(string.Empty, new { saved = true });
        }

        [HttpGet("rollback/{jobId}/confirm")]
        public async Task<IActionResult> ConfirmRollback(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            var activeJob = ResolveActiveJob();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Upgrade Center runs one operation at a time because Bold BI and Bold Reports can share IDP, UMS, ETL, and database resources. " +
                    "Please wait until it finishes before starting a rollback.";
                return RedirectToAction(nameof(Monitor), new { jobId });
            }

            var rollbackEntry = _rollbackStore.GetLatest();
            if (rollbackEntry is null ||
                !rollbackEntry.CanExecute ||
                !string.Equals(rollbackEntry.UpgradeJobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] =
                    "Rollback is only available for the most recent successful upgrade. " +
                    "Older versions cannot be rolled back directly.";
                return RedirectToAction(nameof(Monitor), new { jobId });
            }

            if (!_jobStore.TryGet(jobId, out var backendJob))
            {
                return NotFound($"Job '{jobId}' not found.");
            }
            var selectedProduct = _productContext.SetCurrent(backendJob.ProductKey);

            var rollbackFromVersion = !string.IsNullOrWhiteSpace(rollbackEntry.TargetVersion)
                ? rollbackEntry.TargetVersion
                : backendJob.TargetVersion;
            var restoreToVersion = !string.IsNullOrWhiteSpace(rollbackEntry.PreviousVersion)
                ? rollbackEntry.PreviousVersion
                : backendJob.CurrentVersion;

            var vm = new ConfirmRollbackViewModel
            {
                CurrentUser = user,
                JobId = jobId,
                RollbackId = rollbackEntry.Id,
                RollbackFromVersion = FormatVersionValue(rollbackFromVersion),
                RestoreToVersion = FormatVersionValue(restoreToVersion),
                RollbackPointCreatedAt = rollbackEntry.CreatedAt.UtcDateTime,
                BackupDatabaseCount = rollbackEntry.BackupResult.BackupMappings.Count(mapping => mapping.Succeeded),
                KubernetesImageCount = rollbackEntry.KubernetesResult?.PreviousImages.Count ?? 0,
                Warnings =
                [
                    $"{selectedProduct.DisplayName} will be restored from v{FormatVersionValue(rollbackFromVersion)} to v{FormatVersionValue(restoreToVersion)}.",
                    "Database tables captured in the rollback point will be restored to their pre-upgrade state.",
                    "Recorded Kubernetes deployment images will be reverted and rollout health will be validated.",
                    "Do not stop the Upgrade Center pod while rollback is restoring database tables or deployment images."
                ]
            };

            return View("ConfirmRollback", vm);
        }

        // POST /upgrade-center/rollback/{jobId}: Start rollback

        [HttpPost("rollback/{jobId}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rollback(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            // Block new rollback if any job is already running (single-job constraint).
            var activeJob = ResolveActiveJob();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Upgrade Center runs one operation at a time because Bold BI and Bold Reports can share IDP, UMS, ETL, and database resources. " +
                    "Please wait until it finishes before starting a rollback.";
                return RedirectToAction(nameof(Monitor), new { jobId = jobId });
            }

            // Only allow rollback of the most recent completed upgrade.
            var mostRecent = ResolveMostRecentRollbackableJob();
            if (mostRecent == null || mostRecent.JobId != jobId)
            {
                TempData["ErrorMessage"] =
                    "Rollback is only available for the most recent successful upgrade. " +
                    "Older versions cannot be rolled back directly.";
                return RedirectToAction(nameof(Monitor), new { jobId = jobId });
            }

            if (_jobStore.TryGet(jobId, out var rollbackSourceJob))
            {
                _productContext.SetCurrent(rollbackSourceJob.ProductKey);
            }

            var rollbackJob = await _upgradeService.StartRollbackAsync(jobId, user.DisplayName);
            return RedirectToAction(nameof(Monitor), new { jobId = rollbackJob.JobId });
        }

        // GET /upgrade-center/custom-patch-repository: current common repository for UI pre-fill

        [HttpGet("custom-patch-repository")]
        public async Task<IActionResult> GetDefaultCustomPatchRepository(string? product)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();
            await ResolveProductSelectionAsync(product, HttpContext.RequestAborted);

            try
            {
                var imageRepository = await _upgradeService.GetDefaultCustomPatchRepositoryAsync(HttpContext.RequestAborted);
                return Json(new { imageRepository = imageRepository ?? string.Empty });
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to determine the current custom patch image repository.");
                return Json(new { imageRepository = string.Empty });
            }
        }

        // POST /upgrade-center/validate-patch: AJAX patch image validation

        [HttpPost("validate-patch")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidatePatch(
            [FromForm] string patchVersion,
            [FromForm] string? imageRepository,
            [FromForm] string? imageRef,
            [FromForm] string? product)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();
            var productSelection = await ResolveProductSelectionAsync(product, HttpContext.RequestAborted);
            if (!string.Equals(productSelection.RequestedProduct.Key, productSelection.SelectedProduct.Key, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"{productSelection.RequestedProduct.DisplayName} upgrade is not available for this deployment.");
            }

            var requestedPatchVersion = !string.IsNullOrWhiteSpace(patchVersion) ? patchVersion : imageRef ?? string.Empty;
            var result = await _upgradeService.ValidatePatchImageAsync(requestedPatchVersion, imageRepository);

            var vm = new PatchValidationViewModel
            {
                IsValid        = result.IsValid,
                ErrorMessage   = result.ErrorMessage,
                ParsedVersion  = result.ParsedVersion,
                ImageRepository = result.ImageRepository,
                ImageReference = result.ImageReference,
                Images = result.Images
            };

            return PartialView("_PatchValidationResult", vm);
        }

        // Private helpers

        private async Task<ProductSelection> ResolveProductSelectionAsync(string? productKey, CancellationToken cancellationToken)
        {
            var mode = await _deploymentModeService.ResolveAsync(cancellationToken);
            var requestedProduct = UpgradeProductDefinitions.Resolve(productKey);
            var selectedProduct = mode.AvailableProducts.FirstOrDefault(product =>
                string.Equals(product.Key, requestedProduct.Key, StringComparison.OrdinalIgnoreCase))
                ?? mode.AvailableProducts.FirstOrDefault()
                ?? UpgradeProductDefinitions.BoldBi;

            _productContext.SetCurrent(selectedProduct.Key);
            return new ProductSelection(requestedProduct, selectedProduct, mode.AvailableProducts, mode);
        }

        private sealed record ProductSelection(
            UpgradeProductDefinition RequestedProduct,
            UpgradeProductDefinition SelectedProduct,
            IReadOnlyList<UpgradeProductDefinition> AvailableProducts,
            UpgradeDeploymentModeResult Mode);

        private static string CombineUrl(string baseUrl, string relativePath)
        {
            return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
        }

        private static MonitoringViewModel BuildMonitoringViewModel(
            UpgradeCenterUser user, UiUpgradeJob job)
        {
            var vm = new MonitoringViewModel
            {
                CurrentUser = user,
                Job         = job
            };

            if (job.IsRollback)
            {
                vm.Phase1Label = "Revert deployment";
                vm.Phase2Label = "Restore schema";
                vm.Phase3Label = "Verify health";
            }

            return vm;
        }

        private static bool CanCancelUpgrade(Services.UpgradeJob job)
        {
            return job.UpgradeType != Services.UpgradeJobType.Rollback
                && job.Status is Services.UpgradeJobStatus.Running or Services.UpgradeJobStatus.Cancelling
                && job.CurrentStage != Services.UpgradeJobStageName.AutomaticRollback;
        }

        private void ApplyPlaywrightReportLinks(UiUpgradeJob job)
        {
            foreach (var stage in job.Stages)
            {
                if (string.Equals(stage.Name, "Pre-Upgrade Validation", StringComparison.OrdinalIgnoreCase) &&
                    _playwrightReportStore.Exists(job.JobId, PlaywrightValidationMode.Pre))
                {
                    stage.HtmlReportUrl = Url.Action(nameof(PlaywrightHtmlReport), "UpgradeCenter", new { jobId = job.JobId, stage = "pre-upgrade", assetPath = "report.html" });
                }
                else if (string.Equals(stage.Name, "Post-Upgrade Validation", StringComparison.OrdinalIgnoreCase) &&
                         _playwrightReportStore.Exists(job.JobId, PlaywrightValidationMode.Post))
                {
                    stage.HtmlReportUrl = Url.Action(nameof(PlaywrightHtmlReport), "UpgradeCenter", new { jobId = job.JobId, stage = "post-upgrade", assetPath = "report.html" });
                }
            }
        }

        private string ResolveUploadToken()
        {
            var token = Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";
            return token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? token[bearerPrefix.Length..].Trim()
                : Request.Headers["X-Upgrade-Center-Report-Token"].ToString();
        }

        private static string ComputeSha256Hash(Stream stream)
        {
            var position = stream.CanSeek ? stream.Position : 0;
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.CanSeek)
            {
                stream.Position = position;
            }

            return hash;
        }

        private IActionResult ToUploadAuthorizationFailure(PlaywrightUploadAuthorization authorization)
        {
            var message = authorization.FailureReason ?? "Upload authorization failed.";
            return message.Contains("different upload", StringComparison.OrdinalIgnoreCase)
                ? Conflict(new { saved = false, error = message })
                : Unauthorized(new { saved = false, error = message });
        }

        private void ApplyCompleteOperationLogs(UiUpgradeJob job)
        {
            var entries = _operationLogStore.List(job.JobId, 1000);
            job.CompleteLogEntries = entries.Select(entry => ToLogLine(entry, truncateForDisplay: true)).ToList();
            job.CompleteLogLines = job.CompleteLogEntries.Select(FormatLogLine).ToList();
            job.CompleteLogSequence = entries.Count == 0 ? 0 : entries.Max(entry => entry.Sequence);
        }

        private static OperationLogLine ToLogLine(UpgradeOperationLogEntry entry, bool truncateForDisplay)
        {
            var stage = string.IsNullOrWhiteSpace(entry.Stage) ? "Job" : entry.Stage;
            var message = entry.Message ?? string.Empty;
            var isTruncated = false;
            if (truncateForDisplay && message.Length > CompleteLogDisplayMessageLimit)
            {
                message = message[..CompleteLogDisplayMessageLimit] + $"{Environment.NewLine}... log entry truncated in the browser. Use Download Complete Logs for the full content.";
                isTruncated = true;
            }

            return new OperationLogLine
            {
                Sequence = entry.Sequence,
                Timestamp = entry.CreatedAt.UtcDateTime.ToString("dd MMM yyyy, HH:mm:ss 'UTC'"),
                Level = string.IsNullOrWhiteSpace(entry.Level) ? "Info" : entry.Level,
                Stage = stage,
                Source = string.IsNullOrWhiteSpace(entry.Source) ? "UpgradeCenter" : entry.Source,
                Message = message,
                IsTruncated = isTruncated
            };
        }

        private static string FormatLogLine(OperationLogLine entry)
        {
            return $"[{entry.Timestamp}] [{entry.Level}] [{entry.Stage}]:{Environment.NewLine}{entry.Message}";
        }

        private static string FormatLogLineForDownload(UpgradeOperationLogEntry entry)
        {
            var stage = string.IsNullOrWhiteSpace(entry.Stage) ? "Job" : entry.Stage;
            var level = string.IsNullOrWhiteSpace(entry.Level) ? "Info" : entry.Level;
            return $"[{entry.CreatedAt.UtcDateTime:dd MMM yyyy, HH:mm:ss 'UTC'}] [{level}] [{stage}]{Environment.NewLine}{entry.Message}";
        }

        private static bool TryParsePlaywrightStage(string stage, out PlaywrightValidationMode mode)
        {
            if (string.Equals(stage, "pre-upgrade", StringComparison.OrdinalIgnoreCase))
            {
                mode = PlaywrightValidationMode.Pre;
                return true;
            }

            if (string.Equals(stage, "post-upgrade", StringComparison.OrdinalIgnoreCase))
            {
                mode = PlaywrightValidationMode.Post;
                return true;
            }

            if (string.Equals(stage, "cleanup", StringComparison.OrdinalIgnoreCase))
            {
                mode = PlaywrightValidationMode.Cleanup;
                return true;
            }

            mode = PlaywrightValidationMode.Pre;
            return false;
        }

        private static string ToPlaywrightStageDisplayName(PlaywrightValidationMode mode)
        {
            return mode switch
            {
                PlaywrightValidationMode.Post => "Post-Upgrade Validation",
                PlaywrightValidationMode.Cleanup => "Cleanup Job",
                _ => "Pre-Upgrade Validation"
            };
        }

        /// <summary>
        /// Sets the <c>IsLatestRollbackable</c> flag in ViewData so the
        /// monitoring view can decide whether to render the Rollback button.
        /// Only the most recent completed, in-window upgrade may be rolled back.
        /// </summary>
        private Task ApplyRollbackEligibilityAsync(UiUpgradeJob job)
        {
            bool isLatest = false;
            if (!job.IsRollback && job.CanRollBack)
            {
                var latest = _rollbackStore.GetLatest();
                isLatest = latest?.CanExecute == true &&
                    string.Equals(latest.UpgradeJobId, job.JobId, StringComparison.OrdinalIgnoreCase);
            }
            ViewData["IsLatestRollbackable"] = isLatest;
            return Task.CompletedTask;
        }

        private UiUpgradeJob? ResolveActiveJob()
        {
            return _jobStore.TryGetLatestActive(out var activeJob)
                ? UpgradeJobViewModelMapper.ToViewModel(activeJob)
                : null;
        }

        private UiUpgradeJob? ResolveMostRecentRollbackableJob(string? productKey = null)
        {
            var latest = _rollbackStore.GetLatest();
            if (latest is null || !latest.CanExecute || string.IsNullOrWhiteSpace(latest.UpgradeJobId))
            {
                return null;
            }

            if (!_jobStore.TryGet(latest.UpgradeJobId, out var job))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(productKey) &&
                !string.Equals(UpgradeProductDefinitions.Resolve(job.ProductKey).Key, UpgradeProductDefinitions.Resolve(productKey).Key, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return UpgradeJobViewModelMapper.ToViewModel(job);
        }

        private async Task<List<UpgradeHistoryEntry>> GetHistoryEntriesAsync(
            UpgradeProductDefinition selectedProduct,
            CancellationToken cancellationToken)
        {
            var records = await _historyStore.ListAsync(20, cancellationToken);
            return records
                .Where(record => IsHistoryRecordForProduct(record, selectedProduct))
                .Select(record => new UpgradeHistoryEntry
                {
                    JobId = record.JobId,
                    Label = $"{FormatVersion(record.PreviousVersion)} \u2192 {FormatVersion(record.TargetVersion)}",
                    InitiatedBy = string.IsNullOrWhiteSpace(record.InitiatedByName)
                        ? record.InitiatedByUserId
                        : record.InitiatedByName,
                    Status = UpgradeJobViewModelMapper.ToViewModelStatus(record.Status, record.OperationType),
                    CompletedAt = (record.CompletedAt ?? record.StartedAt).UtcDateTime,
                    HasJobContext = _jobStore.TryGet(record.JobId, out _),
                    IsAutomaticRollback = IsAutomaticRollbackRecord(record)
                })
                .ToList();
        }

        private async Task<List<UpgradeHistoryEntry>> GetHistoryEntriesForDashboardAsync(
            UpgradeProductDefinition selectedProduct,
            CancellationToken cancellationToken)
        {
            try
            {
                return await GetHistoryEntriesAsync(selectedProduct, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Upgrade history could not be loaded for the dashboard. The dashboard will continue without history.");
                ViewData["DashboardWarning"] = "Upgrade history is temporarily unavailable because the IDP or database configuration is not ready. Please refresh after a few seconds.";
                return [];
            }
        }

        private static bool IsHistoryRecordForProduct(
            Services.UpgradeHistoryRecord record,
            UpgradeProductDefinition selectedProduct)
        {
            var product = record.Product?.Trim();
            if (string.IsNullOrWhiteSpace(product))
            {
                return selectedProduct.Product == UpgradeProduct.BoldBi;
            }

            if (selectedProduct.Product == UpgradeProduct.BoldBi)
            {
                return product.Equals(UpgradeProductKeys.BoldBi, StringComparison.OrdinalIgnoreCase) ||
                       product.Equals("BI", StringComparison.OrdinalIgnoreCase) ||
                       product.Equals("BoldBI", StringComparison.OrdinalIgnoreCase) ||
                       product.Equals("Bold BI", StringComparison.OrdinalIgnoreCase);
            }

            return product.Equals(UpgradeProductKeys.BoldReports, StringComparison.OrdinalIgnoreCase) ||
                   product.Equals("Reports", StringComparison.OrdinalIgnoreCase) ||
                   product.Equals("BoldReports", StringComparison.OrdinalIgnoreCase) ||
                   product.Equals("Bold Reports", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAutomaticRollbackRecord(Services.UpgradeHistoryRecord record)
        {
            return string.Equals(record.OperationType, Services.UpgradeHistoryOperationTypes.AutomaticRollback, StringComparison.OrdinalIgnoreCase) ||
                   (string.Equals(record.Status, Services.UpgradeJobStatus.RollbackSucceeded.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(record.OperationType, Services.UpgradeHistoryOperationTypes.ManualRollback, StringComparison.OrdinalIgnoreCase) &&
                    (record.FailureSummary?.Contains("Automatic rollback", StringComparison.OrdinalIgnoreCase) == true ||
                     record.FailureStage is not null));
        }

        private static string FormatVersion(string? version)
        {
            return string.IsNullOrWhiteSpace(version) ? "Unknown" : $"v{version.TrimStart('v', 'V')}";
        }

        private static string FormatVersionValue(string? version)
        {
            return string.IsNullOrWhiteSpace(version) ? "Unknown" : version.TrimStart('v', 'V');
        }
    }
}
