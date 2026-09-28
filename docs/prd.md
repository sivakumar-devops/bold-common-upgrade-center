# Bold Upgrade Center PRD

## Purpose

Bold Upgrade Center is an administrator-facing application that helps customers upgrade an in-cluster Bold BI deployment with guided validation, database protection, Kubernetes rollout monitoring, rollback, operation logs, and upgrade history.

The product goal is to reduce manual upgrade risk by giving administrators a single place to discover available versions, understand database impact, confirm the upgrade, monitor every stage, and recover safely if validation or rollout fails.

## Intended Users

- Bold BI system administrators who are authorized in UMS.
- Support and implementation engineers assisting customers during controlled upgrade windows.
- Operations teams monitoring Kubernetes upgrade and rollback progress.

## Goals

- Show only valid upgrade options newer than the installed Bold BI version.
- Require administrator confirmation before starting any standard release or custom patch upgrade.
- Protect database state before image changes are applied.
- Validate the product before and after upgrade.
- Upgrade only the expected Bold BI Kubernetes deployments.
- Preserve enough job and rollback context to monitor or recover an operation.
- Provide customer-readable progress, logs, reports, and history.
- Prevent concurrent upgrade or rollback operations.

## Non-Goals

- General Kubernetes administration outside the Bold BI deployment set.
- Long-term backup management beyond the active rollback point lifecycle.
- Editing customer database credentials in the UI.
- Replacing Bold BI native schema migration logic.
- Running Playwright tests inside the Upgrade Center web container.

## Functional Requirements

### Authentication and Authorization

- The application must authenticate through the existing Bold ID/UMS session flow.
- Only administrator users may access the Upgrade Center and execute upgrade, rollback, or cancellation actions.
- Authorization must be enforced server-side for all privileged actions.
- If UMS is temporarily unavailable after a successful admin validation, the app may use in-memory pod-lifecycle continuation for monitoring and recovery only.

### Version Discovery

- The application must retrieve release metadata from the configured Bold BI release API.
- It must compare versions using version-aware ordering, not plain string comparison.
- It must show only versions greater than the currently installed version.
- If no newer version is available, the UI must clearly show that the installed version is already the latest.
- The current installed version must be refreshed from the product/version API after a successful upgrade or rollback.

### Standard Release Upgrade

- Clicking Upgrade must open the confirmation workflow and must not immediately start the background job.
- The confirmation page must show the current version, target version, database impact, affected table summary, and warnings.
- The upgrade starts only after the administrator confirms.
- The standard workflow stages are:
  1. Schema Backup
  2. Pre-Upgrade Validation
  3. Kubernetes Upgrade
  4. Post-Upgrade Validation

### Custom Patch Upgrade

- The custom patch workflow must allow a patch tag as the default input.
- If a repository is not provided, the application must derive repositories and image names from the currently running Bold BI deployments.
- If a repository is provided, it must use that repository with discovered image names and the provided tag.
- All generated image references must be validated before the patch upgrade can start.
- Database script lookup and affected-table backup are skipped for custom patch image upgrades unless explicitly introduced in a future requirement.

### Database Impact, Backup, and Restore

- The application must discover the master database and active tenant databases.
- Only active tenants are included when tenant database connections are discovered.
- Duplicate database connections must be ignored.
- Supported database providers are PostgreSQL, MySQL, Microsoft SQL Server, and Oracle.
- For standard release upgrades, the application must analyze database scripts for the selected upgrade range and determine affected tables.
- Existing affected tables must be backed up before Kubernetes image updates.
- Missing affected tables must be skipped with safe warnings instead of failing by default.
- Tables created by upgrade scripts must be tracked for rollback cleanup when that metadata is available.
- Backup databases or backup tables must be created only when at least one existing table requires backup.
- Rollback must restore the backed-up tables to the pre-upgrade state and remove tracked created tables where applicable.
- Plain-text credentials, full connection strings, private keys, tokens, and cookies must not be persisted in Upgrade Center operation records.

### Playwright and Health Validation

- When Playwright Kubernetes Jobs are enabled, pre-upgrade and post-upgrade validation must run in a separate runner image.
- The Upgrade Center must pass runtime configuration through Kubernetes Secrets, not command-line arguments or logs.
- Playwright result summaries must include total, passed, failed, skipped, pass percentage, threshold, and final validation status.
- HTML reports must be uploaded back to the Upgrade Center and served through authenticated endpoints.
- Reports are stored per job and validation stage and retained for the latest configured number of jobs.
- When Playwright Kubernetes Jobs are disabled, validation must use product-level health checks instead of local sample scripts.

### Kubernetes Upgrade

- The application must operate only in the resolved Upgrade Center namespace.
- It must discover expected Bold BI deployments and their containers before patching.
- It must record previous image references before applying new images.
- It must patch and monitor deployments in rollout groups:
  1. id-web-deployment, id-api-deployment, id-ums-deployment
  2. bi-web-deployment, bi-api-deployment, bi-jobs-deployment
  3. bi-dataservice-deployment, bold-ai-deployment, bold-etl-deployment
- Deployments inside a group may run in parallel.
- The next group must start only after the current group succeeds.
- If a required deployment fails or times out, the workflow must stop and trigger the existing rollback path when rollback context is available.
- Rollout timeout is configured per deployment.

### Rollback

- Rollback must be available only for the current valid active rollback point.
- The same rollback point must not be executable repeatedly after successful rollback.
- Automatic rollback must run when a Kubernetes upgrade or post-upgrade validation fails after rollback context has been recorded.
- Manual rollback must require administrator confirmation.
- Rollback stages are:
  1. Restore database
  2. Revert Kubernetes image tags
  3. Wait for Kubernetes rollout completion
  4. Verify deployment health
  5. Complete rollback
- Product health checks must run after Kubernetes rollout succeeds in the Verify deployment health stage.
- Rollback completion or failure must update the rollback job, parent upgrade job, history, and UI consistently.

### Cancellation and Recovery

- Cancellation is available only while an upgrade operation is actively running and before rollback is already in progress.
- Cancelling before deployment image changes are confirmed should stop the job and mark it cancelled.
- Cancelling after deployment images may have changed should use the existing rollback workflow where rollback context exists.
- Rollback operations cannot be cancelled because they restore the system to a safer state.
- Running Playwright jobs and temporary Kubernetes resources must be cleaned up where possible.
- Interrupted jobs must be recoverable from persisted operational context where safe.

### Monitoring, Logs, Reports, and History

- The monitoring UI must show stage status, summary, duration, progress, detailed stage logs, complete operation logs, and report links where available.
- Live logs must append while a job is running and must not expose credentials or sensitive values.
- Operation history must include successful, failed, cancelled, and rolled-back jobs when context is available.
- Open/details links must be available for completed, failed, and rolled-back jobs when job detail context exists.
- Upgrade history must remain available even after old rollback backup files or databases are cleaned up.

## Configuration Requirements

- Product name, release API URL, IDP/UMS service URLs, authentication paths, Kubernetes rollout settings, Playwright runner settings, health-check settings, and report retention must be configurable.
- Admin Playwright credentials must come from Kubernetes Secrets when Playwright jobs are enabled.
- Kubernetes RBAC must be namespace-scoped and limited to the resources required for deployment patching, jobs, pods, pod logs, secrets, and shared state PVCs.

## Success Metrics

- Administrators can complete a supported standard release upgrade without manual Kubernetes commands.
- Failed post-upgrade validation or rollout failure triggers rollback when rollback context exists.
- The UI consistently reflects terminal states without requiring manual refresh.
- Reports and logs are available for troubleshooting without exposing sensitive data.
- Concurrent upgrade and rollback requests are blocked.

## Product-Specific Notes

This PRD applies to Bold Upgrade Center. Bold Reports Upgrade Center uses the same core workflow, but it has different release metadata, deployments, health endpoints, Playwright runner configuration, and product branding.
