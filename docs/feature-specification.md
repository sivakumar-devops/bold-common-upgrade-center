# Bold Upgrade Center Feature Specification

## Overview

Bold Upgrade Center provides a guided upgrade workflow for Bold BI running in Kubernetes. It combines release discovery, database impact analysis, schema backup, validation, Kubernetes image rollout, rollback, logs, reports, and history into one administrator workflow.

## Entry and Navigation

- Base path: `/upgrade-center`.
- The top menu entry is controlled from the Bold ID side and is shown only when the Upgrade Center feature is enabled for Kubernetes hosting.
- The header contains product branding, navigation back to the configured Bold BI/IDP product URL, and the authenticated user menu.
- Main tabs:
  - Available releases
  - Custom patch
  - History
- Supporting pages:
  - Confirm upgrade
  - Upgrade monitoring
  - Rollback confirmation
  - Rollback monitoring
  - Deployment info

## Version Discovery

1. Resolve the installed Bold BI version from the product/version integration.
2. Retrieve available releases from `UpgradeCenter:ReleaseVersionsApiUrl`.
3. Parse version, release date, and release notes URL.
4. Sort versions using version-aware comparison.
5. Display only versions greater than the installed version.
6. Refresh the installed version from the product/version API after successful upgrade or rollback.

If no newer release exists, display a message stating that the installed version is already current.

## Standard Release Flow

### Confirmation

When the administrator clicks Upgrade:

1. Resolve the selected target version.
2. Discover master and active tenant database connections.
3. Determine required database types.
4. Retrieve database scripts for the selected upgrade range.
5. Extract affected tables using the existing script impact logic.
6. Check table existence per database.
7. Show the confirmation dialog with:
   - Current version
   - Target version
   - Database impact
   - Affected table count and sample
   - Upgrade warnings

The upgrade job is not started until the administrator clicks Start Upgrade.

### Background Job

The confirmed upgrade creates a background job. The HTTP request returns quickly, and the UI monitors the job by polling Upgrade Center endpoints.

Stage order:

1. Schema Backup
2. Pre-Upgrade Validation
3. Upgrade
4. Post-Upgrade Validation

## Schema Backup

The Schema Backup stage:

- Discovers master and active tenant databases.
- Deduplicates equivalent database connections.
- Analyzes the selected upgrade range.
- Backs up only existing affected tables.
- Skips missing affected tables with warnings.
- Tracks created tables separately for rollback cleanup where metadata is available.
- Stores backup and rollback context against the upgrade job ID.

If schema impact cannot be determined or required backups fail, the upgrade stops before Kubernetes image changes.

## Pre-Upgrade Validation

When Kubernetes Playwright runner mode is enabled:

- A temporary Kubernetes Secret is created for runtime values.
- A Playwright Kubernetes Job is created in the Upgrade Center namespace.
- A shared state volume is mounted when enabled.
- The runner executes the pre-upgrade suite.
- The runner uploads structured result data and the HTML report to authenticated Upgrade Center endpoints.
- The temporary Secret is removed after the job reaches a terminal state.

When Playwright runner mode is disabled:

- The application runs product health checks through `ProductHealthCheckService`.
- No local sample Playwright script is executed.

If pre-upgrade validation fails, the upgrade stops before Kubernetes image changes. Any newly prepared backup artifacts are cleaned up where safe.

## Kubernetes Upgrade

The Upgrade stage:

1. Resolves the Kubernetes namespace.
2. Retrieves target image details from the release API.
3. Lists current deployments in the namespace.
4. Matches release images to expected Bold BI deployments.
5. Records current image references before patching.
6. Patches deployments in rollout groups.
7. Monitors each deployment rollout until healthy or timed out.

Rollout groups:

1. `id-web-deployment`, `id-api-deployment`, `id-ums-deployment`
2. `bi-web-deployment`, `bi-api-deployment`, `bi-jobs-deployment`
3. `bi-dataservice-deployment`, `bold-ai-deployment`, `bold-etl-deployment`

Deployments within a group run in parallel. Groups run sequentially.

## Post-Upgrade Validation

After Kubernetes upgrade succeeds:

- The post-upgrade Playwright runner job or product health checks are executed.
- Structured result summary and HTML report are captured where available.
- If validation passes, the upgrade is marked successful.
- If validation fails, automatic rollback is started using the recorded rollback context.

## Custom Patch Flow

The Custom patch tab accepts:

- Required patch version/tag.
- Optional image repository.

Behavior:

1. Discover currently running Bold BI deployment images.
2. Reuse existing repository and image names when repository is not supplied.
3. Use the supplied repository when provided.
4. Replace only the image tag with the patch tag.
5. Validate every generated image reference.
6. Enable upgrade only when all required images validate.
7. Skip database script lookup and schema backup for custom patch upgrades.

## Manual Rollback

Manual rollback is available only for the latest active rollback point.

Flow:

1. Administrator opens rollback confirmation.
2. UI displays rollback source, restored version, and warnings.
3. Administrator confirms rollback.
4. Rollback job starts and is monitored separately from the original upgrade job.
5. The rollback point is consumed after successful rollback.

Duplicate rollback requests for the same consumed rollback point must be rejected.

## Automatic Rollback

Automatic rollback is triggered when:

- Kubernetes upgrade fails after rollback context exists.
- Post-upgrade validation fails after image changes.
- Cancellation occurs after image changes and rollback context exists.

Automatic rollback reuses the same database backup and Kubernetes previous-image mappings recorded for the upgrade job.

## Rollback Stages

1. Restore database
2. Revert Kubernetes image tags
3. Wait for Kubernetes rollout completion
4. Verify deployment health
5. Complete rollback

The health verification stage performs product-level HTTP health checks for affected deployments after Kubernetes rollout validation.

## Monitoring and Logs

- Stage cards show stage name, status, summary, duration, details, and report links.
- View Details displays stage-specific progress.
- Complete Operation Logs show job-level live logs.
- Live log content is sanitized to avoid exposing sensitive values.
- Logs are persisted through the operation log store and retrieved by job ID.
- The UI preserves expanded/collapsed state while polling.

## Reports

- Pre-upgrade and post-upgrade reports are stored separately by job ID and stage.
- HTML reports are uploaded from the runner to authenticated Upgrade Center endpoints.
- Report links are shown only when the corresponding report exists.
- Report retention keeps the latest configured number of jobs.

## History

History must show:

- Standard release upgrades
- Custom patch upgrades
- Failed jobs
- Cancelled jobs
- Manual rollback jobs
- Rolled-back jobs

History entries retain audit information even when old physical backup artifacts are cleaned up.

## Security and Data Handling

- Privileged actions require server-side admin authorization.
- Runtime credentials are passed through Kubernetes Secrets.
- Sensitive values must not be written to logs, labels, annotations, command arguments, URLs, or history.
- Report access must remain authenticated.
- Kubernetes permissions must be namespace-scoped and least-privilege.

## Error Handling

- Version API failures show safe user-facing errors.
- Schema impact failures stop before image changes.
- Backup failures stop before image changes.
- Playwright infrastructure failures are treated as validation failures.
- Kubernetes rollout failures trigger rollback when context exists.
- Rollback failures preserve recovery context and history.
